//
//  MethodEngine.swift — безопасная обёртка Swift поверх границы C (method_core.h).
//
//  Слой отвечает ровно за одно: перевод типов. Здесь нет ни ввода-вывода, ни
//  часов, ни единого знания про sing-box, Clash API или конфиги. Всё, что
//  делает файл, — превращает целые коды в типы Swift, коды возврата в throws,
//  а заимствованные строки границы в String, скопированный до того, как
//  владелец умрёт.
//
//  ──────────────────────────────────────────────────────────────────────────
//  ЧТО ОБЁРТКА БЕРЁТ НА СЕБЯ И ЧЕГО НЕ БЕРЁТ
//  ──────────────────────────────────────────────────────────────────────────
//
//  Берёт:
//    • владение указателем движка (освобождение в deinit — единственный путь);
//    • копирование всех строк, выданных границей, в String, пока владелец жив;
//    • освобождение решения и буфера состояния сразу после чтения (defer), так
//      что наружу не уезжает ни один заимствованный указатель;
//    • перевод кодов в ошибки Swift вместе с текстом последней ошибки;
//    • умолчания каталога — читаются У ЯДРА через mc_route_desc_init /
//      mc_lane_desc_init, а не переписаны сюда числами (числа разъедутся молча).
//
//  Не берёт:
//    • ЧАСЫ. Все моменты — миллисекунды МОНОТОННЫХ часов, и подаёт их
//      вызывающий. Обёртка не читает время принципиально: как только решение
//      начинает зависеть от невидимого входа, его нельзя прогнать на записанном
//      логе и нельзя воспроизвести жалобу человека;
//    • ИСПОЛНЕНИЕ. `reconcile` ничего не применяет; пока исполнитель не позвал
//      `laneApplied`, полоса для движка без маршрута;
//    • ПОТОКОБЕЗОПАСНОСТЬ. См. ниже.
//
//  ──────────────────────────────────────────────────────────────────────────
//  ПОТОКИ: КЛАСС НЕ ПОТОКОБЕЗОПАСЕН, И ЭТО ВЫРАЖЕНО В ТИПЕ
//  ──────────────────────────────────────────────────────────────────────────
//
//  `MethodEngine` хранит состояние (каталог, накопленное здоровье, демпфер) и
//  НЕ является Sendable. Отказ от Sendable — не забывчивость: внизу, в ядре,
//  живёт `Cell`, движок не `Sync`, и одновременный вызов граница превращает в
//  ошибку `.busy`. Ниже стоит явное `@available(*, unavailable) extension
//  MethodEngine: Sendable {}`.
//
//  Что именно это даёт, без преувеличений. Везде, где требуется `Sendable`
//  (общий доступ из двух доменов изоляции), сборка падает с адресной фразой
//  «conformance of 'MethodEngine' to 'Sendable' is unavailable» и ссылкой на
//  эту строку — вместо расплывчатого «does not conform». Одностороннюю
//  ПЕРЕДАЧУ владения (движок уехал в задачу и здесь больше не трогается)
//  компилятор по-прежнему разрешает — и правильно: разделения там нет, движок
//  снова у одного хозяина.
//
//  Почему не `actor`. Актор навязал бы `await` каждому вызову — включая те,
//  что и так происходят внутри уже последовательного контекста (таймер
//  измерений, очередь демона). Дешевле и честнее: класс, привязанный к одному
//  исполнителю. Кому нужен доступ из разных задач — оборачивает в свой актор,
//  это десять строк:
//
//      actor Orchestrator {
//          private let engine: MethodEngine
//          init() throws { engine = try MethodEngine(probeIntervalMs: 5_000) }
//          func reconcile(now: UInt64) throws -> Decision {
//              try engine.reconcile(nowMs: now)
//          }
//      }
//
//  `.busy` из такого кода прийти уже не может, и это признак правильной сборки:
//  `.busy` — сообщение «доступ не сериализован», а не повод повторять в цикле.
//
//  ──────────────────────────────────────────────────────────────────────────
//  СБОРКА
//  ──────────────────────────────────────────────────────────────────────────
//
//      cargo build -p method-core-ffi --release       # даёт libmethod_core_ffi.a
//      swiftc -I Core/swift/CMethodCore \
//             -L Core/target/release -lmethod_core_ffi ...
//
//  В Xcode модуль не нужен: достаточно мостового заголовка с
//  `#include "method_core.h"` — `import CMethodCore` под `#if canImport`
//  тогда просто не сработает, а объявления придут глобально.
//

#if canImport(CMethodCore)
import CMethodCore
#endif

// Foundation намеренно не импортируется: обёртке нечего от него взять, а
// состояние отдаётся как [UInt8] — `Data(bytes)` на стороне вызывающего стоит
// одну строку и не тянет фреймворк в слой, который переводит типы.

// ─────────────────────────────── Ошибки ───────────────────────────────

/// Код возврата границы.
///
/// Числа не переписаны сюда руками — они берутся из констант заголовка, чтобы
/// у ABI осталась одна сторона правды.
public enum Status: Sendable, Equatable {
    case ok
    case nullPointer
    case invalidHandle
    case invalidUTF8
    case invalidArgument
    case duplicate
    case notFound
    case stateInvalid
    /// Движок занят вызовом из другого потока. Это НЕ временная беда и не
    /// повод повторять: доступ к движку не сериализован — чините вызывающего.
    case busy
    /// Внутри уже случилась паника. Движку конец: только `deinit`, новый
    /// движок и загрузка сохранённого состояния.
    case poisoned
    /// Паника поймана на границе. Приложение живо.
    case panic
    case internalError
    /// Код, которого обёртка не знает: библиотека новее её.
    case unknown(Int32)

    public init(_ raw: Int32) {
        switch raw {
        case Int32(MC_OK.rawValue): self = .ok
        case Int32(MC_NULL_POINTER.rawValue): self = .nullPointer
        case Int32(MC_INVALID_HANDLE.rawValue): self = .invalidHandle
        case Int32(MC_INVALID_UTF8.rawValue): self = .invalidUTF8
        case Int32(MC_INVALID_ARGUMENT.rawValue): self = .invalidArgument
        case Int32(MC_DUPLICATE.rawValue): self = .duplicate
        case Int32(MC_NOT_FOUND.rawValue): self = .notFound
        case Int32(MC_STATE_INVALID.rawValue): self = .stateInvalid
        case Int32(MC_BUSY.rawValue): self = .busy
        case Int32(MC_POISONED.rawValue): self = .poisoned
        case Int32(MC_PANIC.rawValue): self = .panic
        case Int32(MC_INTERNAL.rawValue): self = .internalError
        default: self = .unknown(raw)
        }
    }

    public var raw: Int32 {
        switch self {
        case .ok: return Int32(MC_OK.rawValue)
        case .nullPointer: return Int32(MC_NULL_POINTER.rawValue)
        case .invalidHandle: return Int32(MC_INVALID_HANDLE.rawValue)
        case .invalidUTF8: return Int32(MC_INVALID_UTF8.rawValue)
        case .invalidArgument: return Int32(MC_INVALID_ARGUMENT.rawValue)
        case .duplicate: return Int32(MC_DUPLICATE.rawValue)
        case .notFound: return Int32(MC_NOT_FOUND.rawValue)
        case .stateInvalid: return Int32(MC_STATE_INVALID.rawValue)
        case .busy: return Int32(MC_BUSY.rawValue)
        case .poisoned: return Int32(MC_POISONED.rawValue)
        case .panic: return Int32(MC_PANIC.rawValue)
        case .internalError: return Int32(MC_INTERNAL.rawValue)
        case .unknown(let raw): return raw
        }
    }
}

/// Отказ библиотеки.
///
/// Текст ошибки живёт в переменной ПОТОКА и действителен лишь до следующего
/// вызова любой функции библиотеки — поэтому он копируется здесь, немедленно,
/// а не хранится указателем. Наружу уезжает обычный String.
public enum EngineError: Error, Sendable, Equatable, CustomStringConvertible {
    /// Отказ границы: код, подробность (может быть пустой) и статический текст
    /// кода.
    case core(status: Status, detail: String, statusText: String)
    /// Библиотека собрана под другую версию ABI, чем эта обёртка. Дальше
    /// работать нельзя: смещения полей могли переехать, и чтение даст мусор,
    /// а не ошибку.
    case abiMismatch(expected: UInt32, found: UInt32)

    public var status: Status? {
        if case .core(let s, _, _) = self { return s }
        return nil
    }

    public var description: String {
        switch self {
        case .core(_, let detail, let text):
            // Подробность библиотеки — законченная фраза («маршрут «lt.trojan.8443»
            // уже объявлен»), и приклеивать к ней классификацию значит получить
            // заикание вроде «состояние не разбирается: состояние не разбирается: …».
            // Классификация никуда не девается: она в `status` и в `statusText`.
            return detail.isEmpty ? text : detail
        case .abiMismatch(let expected, let found):
            return "версия ABI библиотеки \(found), а обёртка написана под \(expected)"
        }
    }
}

// ─────────────────────────────── Дескрипторы ───────────────────────────────

/// Дескриптор маршрута. Отдельный тип, а не `UInt32`, намеренно: в C маршрут и
/// полоса — одинаковые `uint32_t`, и перепутать их местами можно молча. Здесь
/// перепутать нельзя — не соберётся.
public struct RouteHandle: Hashable, Sendable {
    public let raw: UInt32
    fileprivate init(_ raw: UInt32) { self.raw = raw }
}

/// Дескриптор полосы.
public struct LaneHandle: Hashable, Sendable {
    public let raw: UInt32
    fileprivate init(_ raw: UInt32) { self.raw = raw }
}

// ─────────────────────────────── Перечни ───────────────────────────────

/// Ось обхода — независимый СПОСОБ пройти, а не протокол. Запасной маршрут на
/// той же оси запасным не является.
public enum Axis: Sendable, Equatable, CaseIterable {
    /// Hysteria2, TUIC.
    case quicUDP
    /// Reality gRPC.
    case fakeTLSOverH2
    /// Reality Vision.
    case fakeTLSOverTCP
    /// Trojan — подлинный сертификат.
    case realTLS
    /// Shadowsocks-2022 — ни TLS, ни рукопожатия.
    case rawStream
    /// Прямой выход и блокировка: оси не имеют.
    case none

    fileprivate var raw: Int32 {
        switch self {
        case .quicUDP: return MC_AXIS_QUIC_UDP
        case .fakeTLSOverH2: return MC_AXIS_FAKE_TLS_H2
        case .fakeTLSOverTCP: return MC_AXIS_FAKE_TLS_TCP
        case .realTLS: return MC_AXIS_REAL_TLS
        case .rawStream: return MC_AXIS_RAW_STREAM
        case .none: return MC_AXIS_NONE
        }
    }
}

/// Куда выходит трафик.
///
/// Узел выхода уместен только у туннельной экспозиции, поэтому он лежит внутри
/// её случая, а не отдельным полем рядом. В C это два поля, которые можно
/// заполнить несогласованно; здесь несогласованное состояние непредставимо.
public enum Exposure: Sendable, Equatable {
    /// Через наш узел. `exitNode == nil` — выходным считается узел маршрута.
    case tunnelled(exitNode: String? = nil)
    case direct
    case blocked

    fileprivate var raw: Int32 {
        switch self {
        case .tunnelled: return MC_EXPOSURE_TUNNELLED
        case .direct: return MC_EXPOSURE_DIRECT
        case .blocked: return MC_EXPOSURE_BLOCKED
        }
    }
}

/// Множество допустимых экспозиций полосы.
public struct ExposureSet: OptionSet, Sendable {
    public let rawValue: UInt8
    public init(rawValue: UInt8) { self.rawValue = rawValue }

    public static let tunnelled = ExposureSet(rawValue: UInt8(MC_EXPOSURE_TUNNELLED))
    public static let direct = ExposureSet(rawValue: UInt8(MC_EXPOSURE_DIRECT))
    public static let blocked = ExposureSet(rawValue: UInt8(MC_EXPOSURE_BLOCKED))
}

/// Цена установки соединения — влияет на бюджет проб.
public enum HandshakeCost: Sendable, Equatable {
    case cheap
    case expensive

    fileprivate var raw: Int32 {
        switch self {
        case .cheap: return MC_HANDSHAKE_CHEAP
        case .expensive: return MC_HANDSHAKE_EXPENSIVE
        }
    }
}

/// Класс нагрузки: одного числа «качество маршрута» не существует.
public enum SLA: Sendable, Equatable {
    case realtime
    case browse
    case stream
    case bulk
    case sensitive

    fileprivate var raw: Int32 {
        switch self {
        case .realtime: return MC_SLA_REALTIME
        case .browse: return MC_SLA_BROWSE
        case .stream: return MC_SLA_STREAM
        case .bulk: return MC_SLA_BULK
        case .sensitive: return MC_SLA_SENSITIVE
        }
    }

    fileprivate init?(raw: Int32) {
        switch raw {
        case MC_SLA_REALTIME: self = .realtime
        case MC_SLA_BROWSE: self = .browse
        case MC_SLA_STREAM: self = .stream
        case MC_SLA_BULK: self = .bulk
        case MC_SLA_SENSITIVE: self = .sensitive
        default: return nil
        }
    }
}

/// Что делать, когда живых кандидатов не осталось. Прямого выхода здесь нет и
/// быть не может: это была дыра, при которой падение всех маршрутов молча
/// выпускало трафик открытым.
public enum OnEmpty: Sendable, Equatable {
    case block
    case holdLast
    /// Уйти в запасную полосу. Она обязана быть объявлена РАНЬШЕ этой — так
    /// цикл невозможен по построению.
    case fallback(lane: String)

    fileprivate var raw: Int32 {
        switch self {
        case .block: return MC_ON_EMPTY_BLOCK
        case .holdLast: return MC_ON_EMPTY_HOLD_LAST
        case .fallback: return MC_ON_EMPTY_FALLBACK
        }
    }

    fileprivate var targetLane: String? {
        if case .fallback(let lane) = self { return lane }
        return nil
    }

    fileprivate init?(raw: Int32) {
        switch raw {
        case MC_ON_EMPTY_BLOCK: self = .block
        case MC_ON_EMPTY_HOLD_LAST: self = .holdLast
        default: return nil // fallback разбирается отдельно: у него есть цель
        }
    }
}

/// То же решение, но уже принятое: цель названа дескриптором, а не строкой.
public enum EmptyAction: Sendable, Equatable {
    case block
    case holdLast
    case fallback(lane: LaneHandle)
}

/// Рвать ли живые соединения при смене маршрута.
public enum SwitchMode: Sendable, Equatable {
    case drain
    case cut

    fileprivate var raw: Int32 {
        switch self {
        case .drain: return MC_SWITCH_DRAIN
        case .cut: return MC_SWITCH_CUT
        }
    }

    fileprivate init?(raw: Int32) {
        switch raw {
        case MC_SWITCH_DRAIN: self = .drain
        case MC_SWITCH_CUT: self = .cut
        default: return nil
        }
    }
}

/// Почему замер выброшен. Выброшенный замер — НАША неспособность измерить, а
/// не отказ маршрута, и в статистику он не попадает.
public enum DiscardCause: Sendable, Equatable {
    /// Маршрут по умолчанию идёт через туннель — меряем сами себя.
    case defaultRouteThroughTunnel
    case networkChanged
    /// Разрыв в часах: устройство спало.
    case deviceWasAsleep
    case captivePortal
    /// Поднят чужой туннель.
    case foreignTunnel

    fileprivate var raw: Int32 {
        switch self {
        case .defaultRouteThroughTunnel: return MC_DISCARD_DEFAULT_ROUTE_THROUGH_TUNNEL
        case .networkChanged: return MC_DISCARD_NETWORK_CHANGED
        case .deviceWasAsleep: return MC_DISCARD_DEVICE_WAS_ASLEEP
        case .captivePortal: return MC_DISCARD_CAPTIVE_PORTAL
        case .foreignTunnel: return MC_DISCARD_FOREIGN_TUNNEL
        }
    }
}

/// Исход пробы.
public enum ProbeOutcome: Sendable, Equatable {
    case ok(rttMs: Float)
    case timeout
    case handshakeFailed
    /// Трафик вышел не через тот узел. `gotNode == nil` — «вышел мимо, а куда,
    /// неизвестно». Ожидаемый узел не спрашивают: им всегда является узел
    /// самого маршрута.
    case exitMismatch(gotNode: String?)
    case dnsTampered
    case discarded(cause: DiscardCause)
}

/// Метрика — она же объяснение, что именно ограничивает оценку.
public enum Metric: Sendable, Equatable {
    case rtt
    case jitter
    case loss
    case throughput
    case stability

    fileprivate init(raw: Int32) {
        switch raw {
        case MC_METRIC_JITTER: self = .jitter
        case MC_METRIC_LOSS: self = .loss
        case MC_METRIC_THROUGHPUT: self = .throughput
        case MC_METRIC_STABILITY: self = .stability
        default: self = .rtt
        }
    }
}

/// Ворота — некомпенсируемые условия. Провал даёт ровно ноль и список причин,
/// а не низкую оценку: маршрут, подменяющий DNS, не становится приемлемым
/// оттого, что он быстрый.
public struct GateSet: OptionSet, Sendable {
    public let rawValue: UInt32
    public init(rawValue: UInt32) { self.rawValue = rawValue }

    public static let loss = GateSet(rawValue: MC_GATE_LOSS)
    public static let rttTail = GateSet(rawValue: MC_GATE_RTT_TAIL)
    public static let availability = GateSet(rawValue: MC_GATE_AVAILABILITY)
    public static let dnsTampered = GateSet(rawValue: MC_GATE_DNS_TAMPERED)
    public static let exitUnverified = GateSet(rawValue: MC_GATE_EXIT_UNVERIFIED)
    public static let handshakeFailed = GateSet(rawValue: MC_GATE_HANDSHAKE_FAILED)
    public static let ipv6Leak = GateSet(rawValue: MC_GATE_IPV6_LEAK)

    /// Названия сработавших ворот, по-русски.
    ///
    /// Это единственное место, где у ворот вообще есть человеческое имя: ядро
    /// выдаёт словами причины РЕШЕНИЙ, а ворота отдаёт битами. Без списка здесь
    /// каждый вызывающий придумал бы свои формулировки, и в журнале и в
    /// интерфейсе оказались бы разные слова про одно и то же.
    public var namesRu: [String] {
        var out: [String] = []
        if contains(.loss) { out.append("потери выше порога") }
        if contains(.rttTail) { out.append("хвост задержки выше порога") }
        if contains(.availability) { out.append("маршрут недоступен") }
        if contains(.dnsTampered) { out.append("подмена DNS") }
        if contains(.exitUnverified) { out.append("выход не подтверждён") }
        if contains(.handshakeFailed) { out.append("рукопожатие не проходит") }
        if contains(.ipv6Leak) { out.append("утечка IPv6") }
        return out
    }
}

/// Род причины: машинная половина объяснения. По ней интерфейс выбирает значок
/// и решает, показывать ли уведомление.
public enum ReasonKind: Sendable, Equatable {
    /// Первый выбор: держаться было не за что.
    case initial
    /// Сравнение оценок — и «перешли», и «остались».
    case better
    /// Сработал ФАКТ, а не число.
    case emergencyFact
    /// Ось обхода не проходит в этой сети.
    case axisDead
    /// Маршрут отставлен за дребезг.
    case suppressed
    /// Выбор закреплён человеком.
    case userPinned
    case modeChanged
    /// Живого кандидата нет.
    case noCandidate
    /// Подавление снято: отказ хуже дребезга.
    case damperOverridden
    case unknown(Int32)

    fileprivate init(raw: Int32) {
        switch raw {
        case MC_REASON_INITIAL: self = .initial
        case MC_REASON_BETTER: self = .better
        case MC_REASON_EMERGENCY_FACT: self = .emergencyFact
        case MC_REASON_AXIS_DEAD: self = .axisDead
        case MC_REASON_SUPPRESSED: self = .suppressed
        case MC_REASON_USER_PINNED: self = .userPinned
        case MC_REASON_MODE_CHANGED: self = .modeChanged
        case MC_REASON_NO_CANDIDATE: self = .noCandidate
        case MC_REASON_DAMPER_OVERRIDDEN: self = .damperOverridden
        default: self = .unknown(raw)
        }
    }
}

// ─────────────────────────── Описания каталога ───────────────────────────

/// Что маршрут умеет нести.
public struct Carries: Sendable, Equatable {
    public var tcp: Bool
    public var udp: Bool
    public var v4: Bool
    public var v6: Bool

    public init(tcp: Bool, udp: Bool, v4: Bool, v6: Bool) {
        self.tcp = tcp
        self.udp = udp
        self.v4 = v4
        self.v6 = v6
    }

    /// Умолчания ЯДРА, прочитанные через `mc_route_desc_init`, а не списанные
    /// сюда числами: списанные разъедутся с ядром молча, и заметит это уже
    /// человек, у которого маршрут перестал брать UDP.
    public static let `default`: Carries = {
        var d = mc_route_desc_t()
        _ = mc_route_desc_init(&d)
        return Carries(tcp: d.carries_tcp != 0, udp: d.carries_udp != 0,
                       v4: d.carries_v4 != 0, v6: d.carries_v6 != 0)
    }()
}

/// Порог, выдержка и остывание. Нужны ВМЕСТЕ: убрать любое — дребезг
/// возвращается.
public struct Hysteresis: Sendable, Equatable {
    /// Минимальный выигрыш в пунктах; итоговый порог — `max(marginFloor, 2σ)`
    /// по измеренному шуму оценки.
    public var marginFloor: Float
    /// Сколько замеров подряд условие обязано держаться.
    public var dwell: UInt8
    public var cooldownMs: UInt64
    public var cooldownMaxMs: UInt64

    public init(marginFloor: Float, dwell: UInt8, cooldownMs: UInt64, cooldownMaxMs: UInt64) {
        self.marginFloor = marginFloor
        self.dwell = dwell
        self.cooldownMs = cooldownMs
        self.cooldownMaxMs = cooldownMaxMs
    }

    /// Умолчания ядра (`Hysteresis::default`), прочитанные через
    /// `mc_lane_desc_init`.
    public static let `default`: Hysteresis = {
        let d = LaneDescriptor.coreDefaults
        return Hysteresis(marginFloor: d.margin_floor, dwell: d.dwell,
                          cooldownMs: d.cooldown_ms, cooldownMaxMs: d.cooldown_max_ms)
    }()
}

/// Требования полосы к маршруту. Проверяются ДО оценки: не подходящий маршрут
/// не участвует в сравнении вовсе.
public struct RouteRequirements: Sendable, Equatable {
    /// Белый список осей. `nil` — без ограничения. ПУСТОЙ массив — не то же
    /// самое: он запретил бы всё, и граница его отвергнет.
    public var axisIn: [Axis]?
    public var axisNotIn: [Axis]
    /// Белый список стран; `nil` — без ограничения, пустой отвергается.
    public var includeCountry: [String]?
    public var excludeCountry: [String]
    public var requireUDP: Bool
    public var requireV6: Bool

    public init(axisIn: [Axis]? = nil,
                axisNotIn: [Axis] = [],
                includeCountry: [String]? = nil,
                excludeCountry: [String] = [],
                requireUDP: Bool = false,
                requireV6: Bool = false) {
        self.axisIn = axisIn
        self.axisNotIn = axisNotIn
        self.includeCountry = includeCountry
        self.excludeCountry = excludeCountry
        self.requireUDP = requireUDP
        self.requireV6 = requireV6
    }
}

/// Описание маршрута. Единица выбора — МАРШРУТ, а не узел: одна и та же нода
/// по gRPC и по Vision даёт разные задержку и джиттер.
public struct RouteDescriptor: Sendable, Equatable {
    /// `fi.trojan.8443` — свой у каждого маршрута.
    public var id: String
    /// `lt`, `us`, `fi`.
    public var node: String
    /// `hysteria2`, `trojan`, `vless-grpc`.
    public var transport: String
    /// `LT`, `US`, `FI` — идёт и в требования полосы, и в человеческие фразы.
    public var country: String
    public var axis: Axis
    public var exposure: Exposure
    public var handshakeCost: HandshakeCost
    public var carries: Carries

    public init(id: String,
                node: String,
                transport: String,
                country: String,
                axis: Axis,
                exposure: Exposure = .tunnelled(),
                handshakeCost: HandshakeCost = .cheap,
                carries: Carries = .default) {
        self.id = id
        self.node = node
        self.transport = transport
        self.country = country
        self.axis = axis
        self.exposure = exposure
        self.handshakeCost = handshakeCost
        self.carries = carries
    }
}

/// Описание полосы. Полоса — именованный класс трафика, за которым стоит
/// подменяемый набор маршрутов.
public struct LaneDescriptor: Sendable, Equatable {
    public var id: String
    /// Человеческое имя: попадает в объяснения решений дословно.
    public var title: String
    /// Зачем полосе разрешён прямой выход. ОБЯЗАТЕЛЬНО, если `allow` содержит
    /// `.direct`, иначе полоса не создаётся.
    public var justification: String?
    public var sla: SLA
    public var allow: ExposureSet
    public var requirements: RouteRequirements
    /// Минимум независимых ОСЕЙ среди кандидатов: полоса без запаса на другой
    /// оси не имеет запаса вообще.
    public var minAxes: UInt8
    public var onEmpty: OnEmpty
    public var switchMode: SwitchMode
    public var hysteresis: Hysteresis

    public init(id: String,
                title: String,
                justification: String? = nil,
                sla: SLA = LaneDescriptor.defaultSLA,
                allow: ExposureSet = LaneDescriptor.defaultAllow,
                requirements: RouteRequirements = RouteRequirements(),
                minAxes: UInt8 = LaneDescriptor.defaultMinAxes,
                onEmpty: OnEmpty = LaneDescriptor.defaultOnEmpty,
                switchMode: SwitchMode = LaneDescriptor.defaultSwitchMode,
                hysteresis: Hysteresis = .default) {
        self.id = id
        self.title = title
        self.justification = justification
        self.sla = sla
        self.allow = allow
        self.requirements = requirements
        self.minAxes = minAxes
        self.onEmpty = onEmpty
        self.switchMode = switchMode
        self.hysteresis = hysteresis
    }

    /// Умолчания, взятые у ядра одним вызовом. Хранится не сама C-структура (в
    /// ней указатели, и Sendable она не бывает), а разобранные значения.
    fileprivate static let coreDefaults: (sla: Int32, allow: UInt8, minAxes: UInt8,
                                          onEmpty: Int32, switchMode: Int32,
                                          margin_floor: Float, dwell: UInt8,
                                          cooldown_ms: UInt64, cooldown_max_ms: UInt64) = {
        var d = mc_lane_desc_t()
        _ = mc_lane_desc_init(&d)
        return (d.sla, d.allow, d.min_axes, d.on_empty, d.switch_mode,
                d.margin_floor, d.dwell, d.cooldown_ms, d.cooldown_max_ms)
    }()

    public static let defaultSLA: SLA = SLA(raw: coreDefaults.sla) ?? .browse
    /// Умолчание — только через туннель: единственное, при котором ошибка
    /// вызывающего не превращается в открытый трафик.
    public static let defaultAllow = ExposureSet(rawValue: coreDefaults.allow)
    public static let defaultMinAxes: UInt8 = coreDefaults.minAxes
    public static let defaultOnEmpty: OnEmpty = OnEmpty(raw: coreDefaults.onEmpty) ?? .block
    public static let defaultSwitchMode: SwitchMode = SwitchMode(raw: coreDefaults.switchMode) ?? .drain
}

// ─────────────────────────────── Решение ───────────────────────────────

/// Запись журнала причин.
public struct Reason: Sendable, Equatable {
    public let kind: ReasonKind
    /// `nil` означает вердикт про сеть целиком, а не про полосу («в этой сети
    /// не проходит QUIC»). В C это ноль-дескриптор; здесь — честный `nil`.
    public let lane: LaneHandle?
    /// Фраза для человека, по-русски. Копия: строка границы умирает вместе с
    /// решением, а это значение живёт своей жизнью.
    public let text: String
}

/// Действие исполнителю.
///
/// В C у действия есть поля, значимые не всегда: `route` — только у SELECT,
/// `on_empty` — только у GO_EMPTY, причины нет у DRAIN. Здесь такого выбора
/// нет: чего в случае не написано, того и не существует.
public enum Action: Sendable, Equatable {
    /// Поставить полосе маршрут.
    case select(lane: LaneHandle, route: RouteHandle, reason: Reason)
    /// Оборвать живые соединения полосы. Своей причины у обрыва нет: он всегда
    /// следствие соседнего действия.
    case drain(lane: LaneHandle)
    /// Живых кандидатов нет.
    case goEmpty(lane: LaneHandle, action: EmptyAction, reason: Reason)

    public var lane: LaneHandle {
        switch self {
        case .select(let lane, _, _): return lane
        case .drain(let lane): return lane
        case .goEmpty(let lane, _, _): return lane
        }
    }
}

/// Решение целиком.
///
/// Значение, а не окно в память библиотеки: все строки скопированы, само
/// решение уже освобождено. Держать его сколько угодно долго безопасно.
public struct Decision: Sendable, Equatable {
    public let actions: [Action]
    /// Журнал ШИРЕ списка действий: в нём есть и то, почему движок НЕ стал
    /// переключаться, — а это человеку интереснее прочего.
    public let reasons: [Reason]
}

/// Оценка маршрута под класс нагрузки.
public struct Score: Sendable, Equatable {
    /// 0..100. Ровно ноль при провале ворот — это дисквалификация, а не
    /// «плохое качество».
    public let value: Float
    public let band: ClosedRange<Float>?
    /// 0..1. Ниже 0.5 маршрут не участвует в выборе вовсе.
    public let confidence: Float
    public let limiter: Metric
    public let gates: GateSet

    /// Что интерфейсу РАЗРЕШЕНО показать. Числу с холодного старта верить
    /// нельзя, и честнее не показать его вовсе, чем показать красивое.
    public enum Presentation: Sendable, Equatable {
        case measuring
        case band(ClosedRange<Float>)
        case value(Float, limitedBy: Metric)
    }

    public let presentation: Presentation

    /// Ворота сработали — маршрут выбыл, и это не про качество.
    public var isDisqualified: Bool { !gates.isEmpty }
}

// ──────────────────────────── Временная память ────────────────────────────

/// Память, живущая ровно один вызов: сюда копируются строки и массивы, на
/// которые смотрит C.
///
/// Освобождение в `deinit`, а не в `defer` у каждого выделения: так память
/// возвращается и при выбросе ошибки посреди сборки описания, без единого
/// шанса забыть.
private final class Scratch {
    private var blocks: [UnsafeMutableRawPointer] = []

    deinit {
        for b in blocks { b.deallocate() }
    }

    /// Копия строки в UTF-8 без нулевого байта — ровно то, чего ждёт `mc_str_t`.
    func str(_ s: String?) -> mc_str_t {
        guard let s, !s.isEmpty else { return mc_str_t() } // пусто = NULL + 0
        let bytes = Array(s.utf8)
        let buf = UnsafeMutableRawPointer.allocate(byteCount: bytes.count, alignment: 1)
        bytes.withUnsafeBytes { buf.copyMemory(from: $0.baseAddress!, byteCount: bytes.count) }
        blocks.append(buf)
        return mc_str_t(ptr: buf.assumingMemoryBound(to: UInt8.self), len: bytes.count)
    }

    /// Копия массива. Только для простых типов (`Int32`, `mc_str_t`): в `deinit`
    /// память освобождается без вызова деструкторов, потому что их нет.
    func array<T>(_ xs: [T]) -> UnsafePointer<T>? {
        guard !xs.isEmpty else { return nil }
        let p = UnsafeMutablePointer<T>.allocate(capacity: xs.count)
        xs.withUnsafeBufferPointer { p.initialize(from: $0.baseAddress!, count: xs.count) }
        blocks.append(UnsafeMutableRawPointer(p))
        return UnsafePointer(p)
    }
}

// ──────────────────────────────── Движок ────────────────────────────────

/// Оркестратор: каталог маршрутов и полос, накопленные измерения, решения.
///
/// НЕ потокобезопасен и НЕ Sendable — см. шапку файла. Владение указателем
/// единоличное: освобождение только в `deinit`.
public final class MethodEngine {
    /// Версия ABI, под которую написана эта обёртка.
    ///
    /// Числом, а не константой заголовка: макроса `MC_ABI_VERSION` в
    /// `method_core.h` нет — есть функция. Смысл проверки как раз в том, чтобы
    /// сверить ЗАПИСАННОЕ здесь ожидание с тем, что вернёт собранная
    /// библиотека; общая константа сверяла бы число само с собой.
    public static let expectedABIVersion: UInt32 = 1

    /// Версия ABI собранной библиотеки.
    public static var libraryABIVersion: UInt32 { mc_abi_version() }

    private let engine: OpaquePointer

    /// Создать движок.
    ///
    /// `probeIntervalMs` — шаг измерительного контура. Это не «настройка
    /// частоты»: от него зависят множитель возраста в уверенности и порог, за
    /// которым замер считается протухшим. Ноль недопустим — на него делят.
    public init(probeIntervalMs: UInt64) throws {
        // Версию сверяем ДО первого содержательного вызова. Разъехавшийся ABI
        // даёт не ошибку, а мусор из чужого смещения, и ловить его потом
        // придётся по симптомам.
        let found = mc_abi_version()
        guard found == Self.expectedABIVersion else {
            throw EngineError.abiMismatch(expected: Self.expectedABIVersion, found: found)
        }
        var p: OpaquePointer?
        try demand(mc_engine_new(probeIntervalMs, &p))
        // Выходной параметр заполнен ТОЛЬКО при успехе — поэтому читаем его
        // строго после проверки кода, и только тогда `p` не бывает nil.
        guard let p else {
            throw EngineError.core(status: .internalError,
                                   detail: "библиотека вернула успех без движка",
                                   statusText: statusText(Status.internalError.raw))
        }
        engine = p
    }

    deinit {
        // Единственный путь освобождения. Ни free, ни ARC до чужой памяти не
        // дотягиваются: аллокатор Rust и аллокатор Swift — разные аллокаторы.
        mc_engine_free(engine)
    }

    // ─────────────────────────── Каталог ───────────────────────────

    /// Объявить маршрут. Дескриптор действителен всё время жизни движка.
    ///
    /// Удаления нет намеренно: оно либо сдвинуло бы чужие дескрипторы, либо
    /// оставило дыры. Каталог поменялся — `saveState`, новый движок, новый
    /// каталог, `loadState`; история хранится по строковым идентификаторам и
    /// переживает это без потерь.
    @discardableResult
    public func addRoute(_ desc: RouteDescriptor) throws -> RouteHandle {
        let scratch = Scratch()
        var d = mc_route_desc_t()
        // Умолчания берём у ядра, а потом переписываем то, что задано: нулевая
        // структура означала бы «не несёт ни TCP, ни UDP».
        try demand(mc_route_desc_init(&d))
        d.id = scratch.str(desc.id)
        d.node = scratch.str(desc.node)
        d.transport = scratch.str(desc.transport)
        d.country = scratch.str(desc.country)
        if case .tunnelled(let exit) = desc.exposure {
            d.exposure_node = scratch.str(exit)
        }
        d.axis = desc.axis.raw
        d.exposure = desc.exposure.raw
        d.handshake_cost = desc.handshakeCost.raw
        d.carries_tcp = desc.carries.tcp ? 1 : 0
        d.carries_udp = desc.carries.udp ? 1 : 0
        d.carries_v4 = desc.carries.v4 ? 1 : 0
        d.carries_v6 = desc.carries.v6 ? 1 : 0

        var handle: UInt32 = 0
        // withExtendedLifetime обязателен: ARC не видит указателей, выданных
        // Scratch, и вправе освободить его сразу после последнего обращения к
        // объекту — то есть до вызова C.
        try withExtendedLifetime(scratch) {
            try demand(mc_engine_add_route(engine, &d, &handle))
        }
        return RouteHandle(handle)
    }

    /// Объявить полосу.
    @discardableResult
    public func addLane(_ desc: LaneDescriptor) throws -> LaneHandle {
        let scratch = Scratch()
        var d = mc_lane_desc_t()
        try demand(mc_lane_desc_init(&d))
        d.id = scratch.str(desc.id)
        d.title = scratch.str(desc.title)
        d.justification = scratch.str(desc.justification)
        d.on_empty_lane = scratch.str(desc.onEmpty.targetLane)

        // nil и пустой массив здесь значат РАЗНОЕ: nil — «без ограничения»,
        // пустой белый список запретил бы всё.
        //
        // Граница C ловит это сама — по ненулевому указателю при нулевой
        // длине. Но из Swift такого указателя не получить: пустой массив даёт
        // ровно NULL, и проверка на той стороне не сработала бы НИКОГДА, а
        // полоса вместо «не разрешено ничего» молча получила бы «разрешено
        // всё» — противоположность просьбы. Поэтому отказ здесь, теми же
        // словами.
        if let axisIn = desc.requirements.axisIn {
            guard !axisIn.isEmpty else {
                throw reject("пустой белый список осей запретил бы всё; "
                             + "для «без ограничения» передавай nil, а не []")
            }
            d.axis_in = scratch.array(axisIn.map(\.raw))
            d.axis_in_len = axisIn.count
        }
        let axisNotIn = desc.requirements.axisNotIn.map(\.raw)
        d.axis_not_in = scratch.array(axisNotIn)
        d.axis_not_in_len = axisNotIn.count

        if let include = desc.requirements.includeCountry {
            guard !include.isEmpty else {
                throw reject("пустой белый список стран запретил бы всё; "
                             + "для «без ограничения» передавай nil, а не []")
            }
            d.include_country = scratch.array(include.map { scratch.str($0) })
            d.include_country_len = include.count
        }
        let exclude = desc.requirements.excludeCountry.map { scratch.str($0) }
        d.exclude_country = scratch.array(exclude)
        d.exclude_country_len = exclude.count

        d.cooldown_ms = desc.hysteresis.cooldownMs
        d.cooldown_max_ms = desc.hysteresis.cooldownMaxMs
        d.margin_floor = desc.hysteresis.marginFloor
        d.dwell = desc.hysteresis.dwell
        d.sla = desc.sla.raw
        d.on_empty = desc.onEmpty.raw
        d.switch_mode = desc.switchMode.raw
        d.allow = desc.allow.rawValue
        d.min_axes = desc.minAxes
        d.require_udp = desc.requirements.requireUDP ? 1 : 0
        d.require_v6 = desc.requirements.requireV6 ? 1 : 0

        var handle: UInt32 = 0
        try withExtendedLifetime(scratch) {
            try demand(mc_engine_add_lane(engine, &d, &handle))
        }
        return LaneHandle(handle)
    }

    public func routeCount() throws -> Int {
        var n = 0
        try demand(mc_engine_route_count(engine, &n))
        return n
    }

    public func laneCount() throws -> Int {
        var n = 0
        try demand(mc_engine_lane_count(engine, &n))
        return n
    }

    /// Дескриптор маршрута по строковому идентификатору.
    public func routeHandle(id: String) throws -> RouteHandle {
        let scratch = Scratch()
        var h: UInt32 = 0
        let s = scratch.str(id)
        try withExtendedLifetime(scratch) {
            try demand(mc_engine_route_handle(engine, s, &h))
        }
        return RouteHandle(h)
    }

    public func laneHandle(id: String) throws -> LaneHandle {
        let scratch = Scratch()
        var h: UInt32 = 0
        let s = scratch.str(id)
        try withExtendedLifetime(scratch) {
            try demand(mc_engine_lane_handle(engine, s, &h))
        }
        return LaneHandle(h)
    }

    /// Строковый идентификатор маршрута. Возвращается КОПИЯ: строка границы
    /// заимствована у движка, и держать её указателем значит зависеть от того,
    /// что движок не переживёт.
    public func routeID(_ route: RouteHandle) throws -> String {
        var s = mc_str_t()
        try demand(mc_engine_route_id(engine, route.raw, &s))
        return copy(s)
    }

    public func laneID(_ lane: LaneHandle) throws -> String {
        var s = mc_str_t()
        try demand(mc_engine_lane_id(engine, lane.raw, &s))
        return copy(s)
    }

    // ─────────────────────────── Измерения ───────────────────────────

    /// Подать пробу. `atMs` — момент по МОНОТОННЫМ часам.
    public func observe(_ route: RouteHandle, at atMs: UInt64, _ outcome: ProbeOutcome) throws {
        let scratch = Scratch()
        var p = mc_probe_t()
        p.route = route.raw
        p.at_ms = atMs
        switch outcome {
        case .ok(let rtt):
            p.outcome = MC_OUTCOME_OK
            p.rtt_ms = rtt
        case .timeout:
            p.outcome = MC_OUTCOME_TIMEOUT
        case .handshakeFailed:
            p.outcome = MC_OUTCOME_HANDSHAKE_FAILED
        case .exitMismatch(let gotNode):
            p.outcome = MC_OUTCOME_EXIT_MISMATCH
            p.got_node = scratch.str(gotNode)
        case .dnsTampered:
            p.outcome = MC_OUTCOME_DNS_TAMPERED
        case .discarded(let cause):
            p.outcome = MC_OUTCOME_DISCARDED
            p.cause = cause.raw
        }
        try withExtendedLifetime(scratch) {
            try demand(mc_engine_observe(engine, &p))
        }
    }

    /// Подать замер полосы пропускания. Отдельно от пробы: полосу мерят редко и
    /// дорого, задержку — часто и дёшево. Неизмеренная полоса — это НЕ ноль.
    public func observeThroughput(_ route: RouteHandle, at atMs: UInt64, mbps: Float) throws {
        try demand(mc_engine_observe_throughput(engine, route.raw, atMs, mbps))
    }

    /// Сеть сменилась: всё накопленное обесценивается, каталог остаётся.
    /// Задержки через другой канал с прежними несравнимы, а разность между
    /// ними — не джиттер.
    public func networkChanged() throws {
        try demand(mc_engine_network_changed(engine))
    }

    // ─────────────────────────── Решение ───────────────────────────

    /// Свести желаемое с действительным на момент `nowMs`.
    ///
    /// Ничего не исполняет и не считает исполненным: пока не позван
    /// `laneApplied`, полоса для движка без маршрута, и следующий вызов
    /// предложит выбор снова, начислив маршруту штраф за дребезг.
    public func reconcile(nowMs: UInt64) throws -> Decision {
        var raw: OpaquePointer?
        try demand(mc_engine_reconcile(engine, nowMs, &raw))
        guard let raw else {
            throw EngineError.core(status: .internalError,
                                   detail: "библиотека вернула успех без решения",
                                   statusText: statusText(Status.internalError.raw))
        }
        // Решение освобождается здесь же, в этом вызове. Всё, что из него
        // нужно, уже скопировано — наружу не уезжает ни один заимствованный
        // указатель, и вызывающему нечего забыть освободить.
        defer { mc_decision_free(raw) }

        var actionCount = 0
        try demand(mc_decision_action_count(raw, &actionCount))
        var actions: [Action] = []
        actions.reserveCapacity(actionCount)
        for i in 0..<actionCount {
            var a = mc_action_t()
            try demand(mc_decision_action(raw, i, &a))
            let reason = Reason(kind: ReasonKind(raw: a.reason_kind),
                                lane: handle(lane: a.lane),
                                text: copy(a.reason))
            let lane = LaneHandle(a.lane)
            switch a.kind {
            case MC_ACTION_SELECT:
                // Ноль дескриптором не бывает: так граница обозначает «не нашли».
                // Случиться этого не должно — маршрут пришёл из того же каталога,
                // — но молча превратить ноль в дескриптор значит отложить разбор
                // до момента, когда исполнитель получит MC_INVALID_HANDLE без
                // всякого объяснения.
                guard a.route != 0 else {
                    throw EngineError.core(
                        status: .internalError,
                        detail: "решение назвало маршрут, которого нет в каталоге движка",
                        statusText: statusText(Status.internalError.raw))
                }
                actions.append(.select(lane: lane, route: RouteHandle(a.route), reason: reason))
            case MC_ACTION_DRAIN:
                actions.append(.drain(lane: lane))
            case MC_ACTION_GO_EMPTY:
                let what: EmptyAction
                switch a.on_empty {
                case MC_ON_EMPTY_HOLD_LAST: what = .holdLast
                case MC_ON_EMPTY_FALLBACK: what = .fallback(lane: LaneHandle(a.on_empty_lane))
                default: what = .block
                }
                actions.append(.goEmpty(lane: lane, action: what, reason: reason))
            default:
                // Библиотека новее обёртки и знает действие, которого здесь
                // нет. Молча пропустить нельзя: исполнитель недоделает работу
                // и не узнает об этом.
                throw EngineError.core(status: .unknown(a.kind),
                                       detail: "неизвестный род действия \(a.kind): библиотека новее обёртки",
                                       statusText: statusText(Status.internalError.raw))
            }
        }

        var reasonCount = 0
        try demand(mc_decision_reason_count(raw, &reasonCount))
        var reasons: [Reason] = []
        reasons.reserveCapacity(reasonCount)
        for i in 0..<reasonCount {
            var r = mc_reason_t()
            try demand(mc_decision_reason(raw, i, &r))
            reasons.append(Reason(kind: ReasonKind(raw: r.kind),
                                  lane: handle(lane: r.lane),
                                  text: copy(r.text)))
        }

        return Decision(actions: actions, reasons: reasons)
    }

    // ─────────────────────── Состояние полос ───────────────────────

    /// Подтвердить, что маршрут ДЕЙСТВИТЕЛЬНО поставлен полосе.
    ///
    /// Между «движок решил» и «ядро переключило селектор» лежит вызов, который
    /// может не дойти. Подтверждать надо ровно тогда, когда переключение
    /// состоялось: подтверждение авансом заставит движок молчать именно в тот
    /// момент, когда переключения не случилось.
    public func laneApplied(_ lane: LaneHandle, route: RouteHandle) throws {
        try demand(mc_engine_lane_applied(engine, lane.raw, route.raw))
    }

    /// Полоса осталась без маршрута: блокировка, обрыв, отказ применения.
    public func laneCleared(_ lane: LaneHandle) throws {
        try demand(mc_engine_lane_cleared(engine, lane.raw))
    }

    /// Какой маршрут стоит у полосы сейчас; `nil` — никакой.
    public func laneCurrent(_ lane: LaneHandle) throws -> RouteHandle? {
        var r: UInt32 = 0
        try demand(mc_engine_lane_current(engine, lane.raw, &r))
        return r == 0 ? nil : RouteHandle(r)
    }

    /// Закрепить маршрут за полосой до момента `untilMs` (монотонные часы).
    /// Сильнее всей математики, но не сильнее фактов: мёртвый или запрещённый
    /// политикой маршрут движок снимет и скажет об этом словами.
    public func lanePin(_ lane: LaneHandle, route: RouteHandle, untilMs: UInt64) throws {
        try demand(mc_engine_lane_pin(engine, lane.raw, route.raw, untilMs))
    }

    public func laneUnpin(_ lane: LaneHandle) throws {
        try demand(mc_engine_lane_unpin(engine, lane.raw))
    }

    // ─────────────────────────── Оценка ───────────────────────────

    /// Оценка маршрута под класс нагрузки — ровно то же вычисление, каким
    /// пользуется контур решений. Показать человеку число, отличное от того, по
    /// которому принято решение, — верный способ получить вопрос «почему выбран
    /// не тот, у кого больше».
    public func score(_ route: RouteHandle, sla: SLA, nowMs: UInt64) throws -> Score {
        var s = mc_score_t()
        try demand(mc_engine_score(engine, route.raw, sla.raw, nowMs, &s))
        let band: ClosedRange<Float>? = s.has_band != 0 && s.band_lo <= s.band_hi
            ? s.band_lo...s.band_hi
            : nil
        let limiter = Metric(raw: s.limiter)
        let presentation: Score.Presentation
        switch s.display {
        case MC_DISPLAY_VALUE: presentation = .value(s.value, limitedBy: limiter)
        case MC_DISPLAY_BAND: presentation = band.map { .band($0) } ?? .measuring
        default: presentation = .measuring
        }
        return Score(value: s.value,
                     band: band,
                     confidence: s.confidence,
                     limiter: limiter,
                     gates: GateSet(rawValue: s.gates),
                     presentation: presentation)
    }

    // ─────────────────── Состояние между запусками ───────────────────

    /// Выгрузить историю измерений.
    ///
    /// Байты копируются в массив Swift, а буфер библиотеки освобождается тут же
    /// её собственной функцией: два аллокатора не должны встречаться.
    public func saveState(nowMs: UInt64) throws -> [UInt8] {
        var buf = mc_buffer_t()
        try demand(mc_engine_save_state(engine, nowMs, &buf))
        defer { mc_buffer_free(&buf) }
        guard let data = buf.data, buf.len > 0 else { return [] }
        return Array(UnsafeBufferPointer(start: data, count: buf.len))
    }

    /// Загрузить историю измерений.
    ///
    /// `elapsedMs` — сколько РЕАЛЬНОГО времени прошло между сохранением и этой
    /// загрузкой (по стенным часам приложения; ноль — «только что»). Библиотека
    /// часов не читает принципиально, поэтому спрашивает: без этого числа замер
    /// прошлой недели выглядел бы свежим.
    ///
    /// Зачем вообще: без переноса истории каждый запуск даёт две-три минуты,
    /// когда все маршруты одинаково недостоверны и выбор фактически случаен.
    public func loadState(_ bytes: [UInt8], nowMs: UInt64, elapsedMs: UInt64) throws {
        try bytes.withUnsafeBufferPointer { p in
            try demand(mc_engine_load_state(engine, p.baseAddress, p.count, nowMs, elapsedMs))
        }
    }

    // ────────────────────────── Служебное ──────────────────────────

    private func handle(lane raw: UInt32) -> LaneHandle? {
        raw == 0 ? nil : LaneHandle(raw)
    }
}

// Явный отказ от Sendable.
//
// Без этой строки класс тоже не Sendable, но диагностика была бы безымянной
// («does not conform to the 'Sendable' protocol»), и читать её пришлось бы как
// упрёк компилятора. С ней компилятор говорит «conformance … is unavailable» и
// показывает пальцем СЮДА — а здесь написано, почему так и что делать.
// Цена ошибки высока: `.busy` в проде выглядит как случайный сбой библиотеки,
// хотя это несериализованный доступ у вызывающего.
@available(*, unavailable)
extension MethodEngine: Sendable {}

// ──────────────────────── Перевод кодов в ошибки ────────────────────────

/// Скопировать заимствованную строку границы. Владения библиотека не отдаёт
/// никогда, освобождать нечего — но и жить дольше владельца указатель не может,
/// поэтому копия делается сразу.
private func copy(_ s: mc_str_t) -> String {
    guard s.len > 0, let p = s.ptr else { return "" }
    return String(decoding: UnsafeBufferPointer(start: p, count: s.len), as: UTF8.self)
}

/// Статический текст кода возврата — живёт всё время жизни библиотеки.
private func statusText(_ code: Int32) -> String {
    var s = mc_str_t()
    guard mc_status_text(code, &s) == Status.ok.raw else { return "неизвестный код" }
    return copy(s)
}

/// Отказ, выписанный самой обёрткой, а не границей.
///
/// Такие отказы есть ровно там, где различие, значимое для C, при переводе в
/// Swift исчезает (см. пустые белые списки в `addLane`). Классификация берётся
/// та же, что у границы, — вызывающему всё равно, кто именно сказал «нет».
private func reject(_ detail: String) -> EngineError {
    EngineError.core(status: .invalidArgument,
                     detail: detail,
                     statusText: statusText(Status.invalidArgument.raw))
}

/// Код возврата → бросок.
///
/// Порядок чтения важен: сперва текст последней ошибки (он действителен лишь до
/// следующего вызова любой функции библиотеки из этого потока), и только потом
/// статический текст кода.
private func demand(_ rc: Int32) throws {
    guard rc != Status.ok.raw else { return }
    var s = mc_str_t()
    let detail = mc_last_error(&s) == Status.ok.raw ? copy(s) : ""
    throw EngineError.core(status: Status(rc), detail: detail, statusText: statusText(rc))
}
