//
//  EngineScenario.swift — исполняемый сценарий поверх MethodEngine.
//
//  Проходит тот же путь, что реальный клиент, и печатает то, что клиент
//  показал бы человеку. Ничего, кроме печати, сценарий не делает: сети здесь
//  нет, пробы модельные, время модельное.
//
//  ──────────────────────────────────────────────────────────────────────────
//  КАК ЗАПУСКАТЬ
//  ──────────────────────────────────────────────────────────────────────────
//
//      cd "Method VPN/Core"
//      sh swift/run-scenario.sh          # то же самое одной командой
//
//  или руками:
//
//      cargo build -p method-core-ffi --release
//      swiftc -I swift/CMethodCore -L target/release -lmethod_core_ffi \
//             swift/MethodEngine.swift swift/EngineScenario.swift \
//             -o target/release/scenario
//      ./target/release/scenario
//
//  Интерпретатором (`swift file.swift …`) сценарий НЕ запускается, и это не
//  недоделка: JIT подгружает только динамические библиотеки, а ядро собрано
//  статикой (`staticlib` выбран намеренно — в бандле нечего подписывать
//  отдельно и нечего искать в @rpath). Попытка даёт «JIT session error:
//  Symbols not found: _mc_engine_new …». Поэтому шаг компоновки обязателен.
//
//  ──────────────────────────────────────────────────────────────────────────
//  ПОЧЕМУ ВРЕМЯ МОДЕЛЬНОЕ
//  ──────────────────────────────────────────────────────────────────────────
//
//  Часов сценарий не читает — он их СЧИТАЕТ, шагами по пять секунд. Ровно так
//  же устроен прогон на записанном логе: решение обязано быть чистой функцией
//  от входа, иначе жалобу человека нельзя воспроизвести. Побочный выигрыш —
//  вывод сценария одинаков от запуска к запуску, и любое изменение в ядре
//  видно диффом.
//

import Foundation

// ────────────────────────────── Оснастка печати ──────────────────────────────

/// Шаг измерительного контура и он же шаг модельного времени.
private let stepMs: UInt64 = 5_000

private func заголовок(_ s: String) {
    print("\n── \(s) ──")
}

private func строка(_ s: String) {
    print("   \(s)")
}

/// Выровнять по ширине — без Foundation-зависимых фокусов, чтобы файл читался
/// целиком в одном месте.
private func дополнить(_ s: String, _ ширина: Int) -> String {
    s.count >= ширина ? s : s + String(repeating: " ", count: ширина - s.count)
}

private func число(_ v: Float, _ знаков: Int) -> String {
    let множитель = pow(10.0, Float(знаков))
    let округлённое = (v * множитель).rounded() / множитель
    return знаков == 0 ? "\(Int(округлённое))" : "\(округлённое)"
}

/// Каталог, каким его держит сам клиент: дескриптор он получает от движка, а
/// описание оставляет у себя. Движок хранит описание тоже, но наружу отдаёт
/// только идентификатор — и правильно делает: за отображение отвечает тот, кто
/// рисует.
private struct Запись {
    let handle: RouteHandle
    let desc: RouteDescriptor
    /// Базовая задержка модельного канала.
    let baseRtt: Float

    var id: String { desc.id }
}

private func имяОси(_ a: Axis) -> String {
    switch a {
    case .quicUDP: return "QUIC поверх UDP"
    case .fakeTLSOverH2: return "HTTP/2 в поддельном TLS"
    case .fakeTLSOverTCP: return "голый TCP в поддельном TLS"
    case .realTLS: return "настоящий TLS"
    case .rawStream: return "шифрованный поток без рукопожатия"
    case .none: return "без туннеля"
    }
}

private func имяРода(_ k: ReasonKind) -> String {
    switch k {
    case .initial: return "первый выбор"
    case .better: return "сравнение оценок"
    case .emergencyFact: return "факт, а не число"
    case .axisDead: return "ось не проходит"
    case .suppressed: return "отставлен за дребезг"
    case .userPinned: return "закреплено человеком"
    case .modeChanged: return "смена режима"
    case .noCandidate: return "живого кандидата нет"
    case .damperOverridden: return "подавление снято"
    case .unknown(let raw): return "род \(raw)"
    }
}

private func имяМетрики(_ m: Metric) -> String {
    switch m {
    case .rtt: return "задержка"
    case .jitter: return "джиттер"
    case .loss: return "потери"
    case .throughput: return "полоса пропускания"
    case .stability: return "стабильность"
    }
}

// ────────────────────────────── Сам сценарий ──────────────────────────────

@main
enum EngineScenario {

    static func main() {
        do {
            try run()
        } catch {
            print("ОТКАЗ: \(error)")
            exit(1)
        }
    }

    static func run() throws {
        print("Версия ABI библиотеки: \(MethodEngine.libraryABIVersion), "
              + "обёртка написана под \(MethodEngine.expectedABIVersion)")

        let движок = try MethodEngine(probeIntervalMs: stepMs)

        // ── 1. Каталог: пять маршрутов на ПЯТИ разных осях обхода. ──
        //
        // Оси, а не протоколы: маршруты на одной оси убивает одна и та же
        // причина, и запасом друг другу они не являются. Задержки взяты
        // правдоподобные — Vision заметно хуже gRPC на той же ноде, как и в
        // замерах с литовской ноды.
        заголовок("Каталог")

        var каталог: [Запись] = []
        func маршрут(_ desc: RouteDescriptor, rtt: Float) throws {
            let h = try движок.addRoute(desc)
            каталог.append(Запись(handle: h, desc: desc, baseRtt: rtt))
            строка(дополнить(desc.id, 20)
                   + "ось «\(имяОси(desc.axis))», \(desc.country), ~\(Int(rtt)) мс"
                   + (desc.carries.udp ? ", несёт UDP" : ""))
        }

        try маршрут(RouteDescriptor(
            id: "fi.hysteria2.443", node: "fi", transport: "hysteria2", country: "FI",
            axis: .quicUDP, handshakeCost: .cheap,
            carries: Carries(tcp: true, udp: true, v4: true, v6: false)), rtt: 38)
        try маршрут(RouteDescriptor(
            id: "lt.grpc.2083", node: "lt", transport: "vless-grpc", country: "LT",
            axis: .fakeTLSOverH2, handshakeCost: .expensive,
            carries: Carries(tcp: true, udp: false, v4: true, v6: false)), rtt: 62)
        try маршрут(RouteDescriptor(
            id: "fi.vision.443", node: "fi", transport: "vless-vision", country: "FI",
            axis: .fakeTLSOverTCP, handshakeCost: .expensive,
            carries: Carries(tcp: true, udp: false, v4: true, v6: false)), rtt: 96)
        try маршрут(RouteDescriptor(
            id: "lt.trojan.8443", node: "lt", transport: "trojan", country: "LT",
            axis: .realTLS, handshakeCost: .expensive,
            carries: Carries(tcp: true, udp: false, v4: true, v6: false)), rtt: 74)
        try маршрут(RouteDescriptor(
            id: "lt.ss2022.2095", node: "lt", transport: "shadowsocks-2022", country: "LT",
            axis: .rawStream, handshakeCost: .cheap,
            carries: Carries(tcp: true, udp: true, v4: true, v6: false)), rtt: 88)

        // Полоса «Веб» — обычный трафик, любой маршрут через туннель.
        let веб = try движок.addLane(LaneDescriptor(id: "web", title: "Веб", sla: .browse))

        // Полоса «Звонки» — realtime и требование UDP. Кандидатов у неё всего
        // два: Hysteria2 и Shadowsocks-2022. Остальные три маршрута UDP не
        // несут и в сравнении не участвуют вовсе.
        let звонки = try движок.addLane(LaneDescriptor(
            id: "voice", title: "Звонки", sla: .realtime,
            requirements: RouteRequirements(requireUDP: true)))

        строка("")
        строка("полос: \(try движок.laneCount()), маршрутов: \(try движок.routeCount())")

        // ── 2. Девяносто раундов измерений. ──
        //
        // Меньше нельзя: уверенность набирается из числа проб и ширины
        // интервала, а порог участия в выборе — 0.5. Мелкая рябь нужна, чтобы
        // перцентили были перцентилями, а не одним и тем же числом.
        var сейчас: UInt64 = 0
        func раунд(мёртвые: Set<String> = []) throws {
            сейчас += stepMs
            let рябь = Float((сейчас / stepMs) % 5)
            for з in каталог {
                if мёртвые.contains(з.id) {
                    try движок.observe(з.handle, at: сейчас, .timeout)
                } else {
                    try движок.observe(з.handle, at: сейчас, .ok(rttMs: з.baseRtt + рябь))
                }
            }
        }

        заголовок("Измерения")
        for _ in 0..<90 { try раунд() }
        строка("90 раундов по \(stepMs / 1000) с — модельное время \(сейчас / 1000) с")

        // ── 3. Первое решение. ──
        заголовок("Первое решение")
        try показатьРешение(движок, движок.reconcile(nowMs: сейчас))

        // ── 4. Активный маршрут умирает. ──
        //
        // Три пробы подряд без ответа — это ФАКТ, а не число. Он обходит порог,
        // выдержку и остывание целиком: оценка не имеет права удержать
        // человека на мёртвом маршруте.
        заголовок("Hysteria2 перестала отвечать (три пробы подряд)")
        for _ in 0..<3 { try раунд(мёртвые: ["fi.hysteria2.443"]) }
        try показатьРешение(движок, движок.reconcile(nowMs: сейчас))

        // ── 5. Умирает и второй UDP-маршрут. ──
        //
        // У полосы «Звонки» живых кандидатов не остаётся вовсе. Проверяем не
        // «что-нибудь произошло», а что произошло БЕЗОПАСНОЕ: трафик полосы
        // блокируется, а не выпускается открытым. Прямого выхода в перечне
        // действий при пустоте нет и быть не может.
        заголовок("Умер и Shadowsocks — у «Звонков» не осталось UDP-маршрутов")
        for _ in 0..<3 { try раунд(мёртвые: ["fi.hysteria2.443", "lt.ss2022.2095"]) }
        try показатьРешение(движок, движок.reconcile(nowMs: сейчас))

        // ── 6. Оценки. ──
        //
        // Ровно те же числа, по которым принято решение выше. Показать другие —
        // верный способ получить вопрос «почему выбран не тот, у кого больше».
        заголовок("Оценки под класс «веб»")
        for з in каталог {
            let s = try движок.score(з.handle, sla: .browse, nowMs: сейчас)
            var хвост: String
            switch s.presentation {
            case .measuring:
                хвост = "измеряем, числа нет"
            case .band(let диапазон):
                хвост = "\(число(диапазон.lowerBound, 0))…\(число(диапазон.upperBound, 0)) (диапазон)"
            case .value(let v, let ограничитель):
                хвост = "\(число(v, 1)), ограничивает \(имяМетрики(ограничитель))"
            }
            if s.isDisqualified {
                хвост = "0 — дисквалифицирован: " + s.gates.namesRu.joined(separator: ", ")
            }
            строка(дополнить(з.id, 20) + "уверенность \(число(s.confidence, 2))   " + хвост)
        }

        // ── 7. Состояние переживает перезапуск. ──
        //
        // Без переноса истории каждый запуск даёт две-три минуты, когда все
        // маршруты одинаково недостоверны и выбор фактически случаен.
        заголовок("Перезапуск")
        let состояние = try движок.saveState(nowMs: сейчас)
        строка("состояние: \(состояние.count) байт")

        // Новый движок — новая эпоха монотонных часов. Каталог объявляем
        // заново теми же описаниями, историю подсовываем обратно: она хранится
        // по строковым идентификаторам и переживает смену дескрипторов.
        //
        // `после` — не ноль намеренно. Монотонные часы на Apple считают время
        // работы системы, и приложение стартует не в нулевой момент. Число тут
        // значимо: замер, чей возраст больше нынешнего `now`, теряет отметку
        // времени целиком, — то есть при `now = 0` любая история выглядела бы
        // как её отсутствие.
        let после: UInt64 = 60_000
        let свежий = try MethodEngine(probeIntervalMs: stepMs)
        let сИсторией = try MethodEngine(probeIntervalMs: stepMs)
        for заново in [свежий, сИсторией] {
            for з in каталог { _ = try заново.addRoute(з.desc) }
        }
        // Второй аргумент — сколько РЕАЛЬНОГО времени прошло между сохранением
        // и загрузкой: приложение перезапустили через секунду.
        try сИсторией.loadState(состояние, nowMs: после, elapsedMs: 1_000)

        let без = try свежий.score(свежий.routeHandle(id: "lt.grpc.2083"),
                                   sla: .browse, nowMs: после)
        let с = try сИсторией.score(сИсторией.routeHandle(id: "lt.grpc.2083"),
                                    sla: .browse, nowMs: после)
        строка("уверенность после перезапуска: без истории \(число(без.confidence, 2)), "
               + "с историей \(число(с.confidence, 2))")

        // ── 8. Отказы, которые обязаны быть отказами. ──
        //
        // Обёртка границу не «смягчает»: неверный ввод остаётся ошибкой, и
        // приходит она текстом, а не кодом. Сфабриковать дескриптор из числа,
        // к слову, нельзя вовсе — у RouteHandle нет открытого инициализатора,
        // и такой ошибки в этом списке просто не бывает.
        заголовок("Отказы")
        проверитьОтказ("полоса с прямым выходом без объяснения") {
            _ = try движок.addLane(LaneDescriptor(id: "leaky", title: "Дырявая",
                                                  allow: [.tunnelled, .direct]))
        }
        проверитьОтказ("повторный идентификатор маршрута") {
            _ = try движок.addRoute(RouteDescriptor(id: "lt.trojan.8443", node: "lt",
                                                    transport: "trojan", country: "LT",
                                                    axis: .realTLS))
        }
        проверитьОтказ("прямой выход, объявленный на оси обхода") {
            _ = try движок.addRoute(RouteDescriptor(id: "x.direct", node: "lt",
                                                    transport: "direct", country: "LT",
                                                    axis: .realTLS, exposure: .direct))
        }
        проверитьОтказ("пустой белый список стран") {
            _ = try движок.addLane(LaneDescriptor(
                id: "nowhere", title: "Никуда",
                requirements: RouteRequirements(includeCountry: [])))
        }
        проверитьОтказ("полоса, которой не объявляли") {
            _ = try движок.laneHandle(id: "нет-такой")
        }
        проверитьОтказ("битое сохранённое состояние") {
            try движок.loadState(Array("{не json".utf8), nowMs: сейчас, elapsedMs: 0)
        }

        // ── 9. Дескрипторы в обе стороны. ──
        заголовок("Дескрипторы")
        строка("полоса \(веб.raw) → «\(try движок.laneID(веб))», "
               + "полоса \(звонки.raw) → «\(try движок.laneID(звонки))»")
        if let текущий = try движок.laneCurrent(веб) {
            строка("у полосы «Веб» стоит \(try движок.routeID(текущий))")
        }
        if try движок.laneCurrent(звонки) == nil {
            строка("у полосы «Звонки» не стоит ничего — и это правильный итог")
        }

        print("\nСценарий пройден.")
    }

    // ── Печать решения ──

    /// Печатает решение так, как показал бы клиент, и подтверждает применение.
    ///
    /// Подтверждение — не формальность: пока исполнитель не позвал
    /// `laneApplied`, полоса для движка без маршрута, и следующее решение
    /// предложит выбор снова, начислив маршруту штраф за дребезг. Здесь
    /// применение всегда удаётся, поэтому подтверждается сразу; у настоящего
    /// исполнителя между решением и подтверждением лежит вызов, который может
    /// не дойти.
    static func показатьРешение(_ движок: MethodEngine, _ решение: Decision) throws {
        if решение.actions.isEmpty {
            строка("действий нет — движок решил ничего не менять")
        }
        for действие in решение.actions {
            switch действие {
            case .select(let полоса, let маршрут, let причина):
                строка("НАЗНАЧИТЬ  «\(try движок.laneID(полоса))» → \(try движок.routeID(маршрут))")
                строка("           \(причина.text)")
                строка("           [\(имяРода(причина.kind))]")
                try движок.laneApplied(полоса, route: маршрут)
            case .drain(let полоса):
                строка("ОБОРВАТЬ   живые соединения полосы «\(try движок.laneID(полоса))»")
            case .goEmpty(let полоса, let что, let причина):
                let итог: String
                switch что {
                case .block: итог = "блокировать трафик"
                case .holdLast: итог = "держать прежний маршрут и громко сказать"
                case .fallback(let куда): итог = "уйти в полосу «\(try движок.laneID(куда))»"
                }
                строка("ПУСТО      «\(try движок.laneID(полоса))»: \(итог)")
                строка("           \(причина.text)")
                строка("           [\(имяРода(причина.kind))]")
                if case .block = что {
                    try движок.laneCleared(полоса)
                }
            }
        }

        // Журнал ШИРЕ списка действий: в нём есть и то, почему движок НЕ стал
        // переключаться. Человеку это интереснее прочего.
        if !решение.reasons.isEmpty {
            строка("")
            строка("журнал причин:")
            for причина in решение.reasons {
                let где = причина.lane.flatMap { try? движок.laneID($0) } ?? "сеть целиком"
                строка("  • [\(где)] \(причина.text)")
            }
        }
    }

    static func проверитьОтказ(_ что: String, _ действие: () throws -> Void) {
        do {
            try действие()
            строка("НЕ ОТКАЗАЛ  \(что)")
        } catch {
            строка("отказ: \(что)")
            строка("       \(error)")
        }
    }
}
