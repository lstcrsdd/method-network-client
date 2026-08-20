import Foundation

/// Политика: какой трафик по какой полосе идёт.
///
/// Это модель пользовательских сценариев. Правило называет ПОЛОСУ, а не
/// сервер: «банк — напрямую», «игры — по самому ровному пути», а какой
/// маршрут сегодня удовлетворяет требование, решает управляющий слой.
/// Поэтому правила не приходится переписывать при смене нод, а смена
/// маршрута не трогает правила.
public struct LanePolicy: Equatable {

    /// Что делать, когда у полосы не осталось живых маршрутов.
    ///
    /// Прямого выхода здесь нет НАМЕРЕННО и добавить его нельзя. Это была
    /// дыра, из-за которой падение всех маршрутов молча выпускало трафик
    /// открытым: разрешение на прямой выход требуется правилу, а «что делать
    /// при пустоте» — не правило, и проверка грантов его не покрывала.
    public enum OnEmpty: Equatable {
        case block
        case fallback(lane: String)
    }

    public struct Lane: Equatable {
        public var id: String
        public var title: String
        /// Разрешён ли этой полосе выход открытым.
        public var allowsDirect: Bool
        /// Обязательно для полосы с прямым выходом. Печатается в интерфейсе
        /// рядом с правилом: «56 доменов» человек проматывает, а объяснение
        /// «зачем» заставляет остановиться.
        public var justification: String?
        /// Ограничение по осям обхода. nil — любые.
        public var axisIn: [LaneConfigBuilder.EvasionAxisKey]?
        /// Минимум независимых осей среди кандидатов. Полоса без запаса на
        /// другой оси не имеет запаса вообще: маршруты одной оси умирают от
        /// одной причины.
        public var minAxes: Int
        public var onEmpty: OnEmpty

        public init(id: String, title: String, allowsDirect: Bool = false,
                    justification: String? = nil,
                    axisIn: [LaneConfigBuilder.EvasionAxisKey]? = nil,
                    minAxes: Int = 1, onEmpty: OnEmpty = .block) {
            self.id = id
            self.title = title
            self.allowsDirect = allowsDirect
            self.justification = justification
            self.axisIn = axisIn
            self.minAxes = minAxes
            self.onEmpty = onEmpty
        }
    }

    /// Условие правила. Только атрибуты потока — ничего про здоровье
    /// маршрутов: решение принимается на каждое соединение, тысячи раз в
    /// секунду, и знание о здоровье там выразить нечем.
    public enum Match: Equatable {
        case domainSuffix([String])
        case domainKeyword([String])
        case processName([String])
        case port([Int])
        case ipIsPrivate
    }

    public struct Flow: Equatable {
        public var match: Match
        public var lane: String
        public var note: String?

        public init(match: Match, lane: String, note: String? = nil) {
            self.match = match
            self.lane = lane
            self.note = note
        }
    }

    public var lanes: [Lane]
    public var flows: [Flow]
    /// Полоса для всего остального. Не Optional намеренно: правило на
    /// остаток невозможно забыть, потому что его нельзя не указать.
    public var defaultLane: String

    public init(lanes: [Lane], flows: [Flow], defaultLane: String) {
        self.lanes = lanes
        self.flows = flows
        self.defaultLane = defaultLane
    }

    /// Заводская политика: одна полоса, весь трафик в туннель, локальная сеть
    /// напрямую. Ровно то, что человек получает, ничего не настроив.
    public static func factory() -> LanePolicy {
        LanePolicy(
            lanes: [
                Lane(id: "web", title: "Весь трафик", minAxes: 2),
                Lane(id: "lan", title: "Локальная сеть", allowsDirect: true,
                     justification: "Принтеры, NAS и шлюз в домашней сети"),
            ],
            flows: [Flow(match: .ipIsPrivate, lane: "lan")],
            defaultLane: "web"
        )
    }

    // MARK: - Проверка

    public enum ValidationError: Error, LocalizedError, Equatable {
        case unknownLane(String, inRule: String)
        case defaultLaneMissing(String)
        case defaultLaneAllowsDirect(String)
        case directWithoutJustification(String)
        case emptyLanes
        case duplicateLane(String)
        case fallbackToDirectLane(from: String, to: String)
        case fallbackCycle(String)
        case notEnoughAxes(lane: String, need: Int, have: Int)

        public var errorDescription: String? {
            switch self {
            case .unknownLane(let l, let r):
                return "Правило «\(r)» ссылается на несуществующую полосу «\(l)»"
            case .defaultLaneMissing(let l):
                return "Полоса для остального трафика «\(l)» не объявлена"
            case .defaultLaneAllowsDirect(let l):
                return "Полоса «\(l)» принимает весь остальной трафик и при этом "
                    + "разрешает прямой выход — так весь неописанный трафик пойдёт открытым"
            case .directWithoutJustification(let l):
                return "У полосы «\(l)» разрешён прямой выход без объяснения зачем"
            case .emptyLanes:
                return "В политике нет ни одной полосы"
            case .duplicateLane(let l):
                return "Полоса «\(l)» объявлена дважды"
            case .fallbackToDirectLane(let from, let to):
                return "Полоса «\(from)» при отказе уходит в «\(to)», а та выпускает трафик открытым"
            case .fallbackCycle(let l):
                return "Цепочка запасных полос зациклена на «\(l)»"
            case .notEnoughAxes(let lane, let need, let have):
                return "Полосе «\(lane)» нужно независимых способов обхода: \(need), "
                    + "а среди подходящих маршрутов их \(have)"
            }
        }
    }

    /// Проверяет политику и бросает, а не предупреждает.
    ///
    /// Отказ собрать — не строгость ради строгости. Профиль маршрутизации без
    /// явного правила на остаток однажды превратил «весь трафик в туннель» в
    /// «весь трафик напрямую» сразу у всех, кто его получил, — при этом
    /// клиент показывал «подключено». Такое должно быть невозможно собрать, а
    /// не замечено при разборе аварии.
    /// Правила, до которых очередь не дойдёт никогда.
    ///
    /// Ядро проверяет правила сверху вниз и берёт первое совпавшее. Значит
    /// правило, все значения которого уже перечислены выше, не сработает —
    /// причём молча: человек видит строку в списке и считает, что она
    /// действует. Именно так и вышло при первой живой проверке: старое
    /// правило «2ip.io → весь трафик» перекрыло новое «2ip.io → мимо
    /// туннеля», и разницы в поведении не было.
    ///
    /// Возвращает индексы перекрытых правил вместе с индексом того, кто их
    /// перекрыл, — интерфейсу нужно назвать виновника поимённо.
    public func shadowedFlows() -> [(index: Int, by: Int)] {
        var out: [(index: Int, by: Int)] = []
        for (i, flow) in flows.enumerated() {
            guard let mine = Self.values(of: flow.match), !mine.isEmpty else { continue }
            for (j, earlier) in flows.enumerated() where j < i {
                guard Self.kind(of: earlier.match) == Self.kind(of: flow.match),
                      let theirs = Self.values(of: earlier.match) else { continue }
                if mine.allSatisfy(theirs.contains) {
                    out.append((index: i, by: j))
                    break
                }
            }
        }
        return out
    }

    private static func kind(of match: Match) -> String {
        switch match {
        case .domainSuffix:  return "domainSuffix"
        case .domainKeyword: return "domainKeyword"
        case .processName:   return "processName"
        case .port:          return "port"
        case .ipIsPrivate:   return "private"
        }
    }

    private static func values(of match: Match) -> Set<String>? {
        switch match {
        case .domainSuffix(let v), .domainKeyword(let v), .processName(let v):
            return Set(v.map { $0.lowercased() })
        case .port(let v):   return Set(v.map(String.init))
        case .ipIsPrivate:   return nil
        }
    }

    public func validate(availableAxes: Set<LaneConfigBuilder.EvasionAxisKey>) throws {
        guard !lanes.isEmpty else { throw ValidationError.emptyLanes }

        var seen = Set<String>()
        for lane in lanes {
            guard seen.insert(lane.id).inserted else {
                throw ValidationError.duplicateLane(lane.id)
            }
            if lane.allowsDirect,
               (lane.justification?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true) {
                throw ValidationError.directWithoutJustification(lane.id)
            }
        }

        let byID = Dictionary(uniqueKeysWithValues: lanes.map { ($0.id, $0) })
        guard let fallbackLane = byID[defaultLane] else {
            throw ValidationError.defaultLaneMissing(defaultLane)
        }
        if fallbackLane.allowsDirect {
            throw ValidationError.defaultLaneAllowsDirect(defaultLane)
        }

        for flow in flows {
            guard byID[flow.lane] != nil else {
                throw ValidationError.unknownLane(flow.lane, inRule: describe(flow.match))
            }
        }

        // Цепочка запасных полос: не ведёт в прямой выход и не зациклена.
        for lane in lanes {
            var visited: Set<String> = [lane.id]
            var current = lane
            while case .fallback(let next) = current.onEmpty {
                guard let target = byID[next] else {
                    throw ValidationError.unknownLane(next, inRule: "запасная полоса для «\(current.id)»")
                }
                if target.allowsDirect {
                    throw ValidationError.fallbackToDirectLane(from: current.id, to: next)
                }
                guard visited.insert(next).inserted else {
                    throw ValidationError.fallbackCycle(next)
                }
                current = target
            }
        }

        // Осей должно хватать на требование полосы — иначе «запасной путь»
        // существует только на бумаге.
        for lane in lanes where !lane.allowsDirect {
            let usable = lane.axisIn.map { Set($0).intersection(availableAxes) } ?? availableAxes
            if usable.count < lane.minAxes {
                throw ValidationError.notEnoughAxes(lane: lane.id, need: lane.minAxes,
                                                    have: usable.count)
            }
        }
    }

    private func describe(_ match: Match) -> String {
        switch match {
        case .domainSuffix(let d):   return "домены " + d.prefix(2).joined(separator: ", ")
        case .domainKeyword(let d):  return "подстроки " + d.prefix(2).joined(separator: ", ")
        case .processName(let p):    return "приложения " + p.prefix(2).joined(separator: ", ")
        case .port(let p):           return "порты " + p.prefix(3).map(String.init).joined(separator: ", ")
        case .ipIsPrivate:           return "локальная сеть"
        }
    }
}
