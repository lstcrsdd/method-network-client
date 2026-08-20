import Foundation

/// Кто решает, какой маршрут стоит за полосой.
///
/// Шов заведён специально, и вот зачем. Настоящий движок выбора — ядро
/// оркестратора на Rust: там оценка качества по семи метрикам с
/// некомпенсируемыми воротами, гистерезис, штраф за дребезг и классификация
/// причины отказа по оси. Пока библиотека не собрана и не подключена, соблазн
/// «пока прикинем формулу на Swift» велик — и это была бы худшая из ошибок:
/// две реализации оценки неизбежно разъедутся, а доказательства свойств будут
/// относиться к той, которая не принимает решений.
///
/// Поэтому временная реализация ниже намеренно НЕ считает качество вовсе.
/// Она делает ровно то, что нельзя сделать неправильно, и честно об этом
/// сообщает.
public protocol LaneDecider: Sendable {
    /// Наблюдение по маршруту. Всё, что решающий слой имеет право знать.
    func observe(_ sample: RouteSample) async

    /// Какой член назначить каждой полосе на этот момент.
    /// Пустой ответ означает «менять нечего», а не «кандидатов нет».
    func decide(lanes: [String: [String]], now: Date) async -> [LaneDecision]

    /// Чем движок объясняет своё состояние человеку.
    func explain() async -> String
}

/// Одна проба маршрута.
public struct RouteSample: Sendable {
    public enum Outcome: Sendable, Equatable {
        case ok(rttMS: Int)
        /// Не ответил за отведённое время.
        case timeout
        /// Замер недостоверен и НЕ говорит о маршруте ничего.
        ///
        /// Отдельный случай, а не разновидность отказа: недостоверный замер,
        /// засчитанный как отказ, наказывает исправный узел за нашу же
        /// ошибку измерения. Ровно это дважды стоило проекту полдня.
        case discarded(reason: String)
    }

    public let route: String
    public let at: Date
    public let outcome: Outcome

    public init(route: String, at: Date, outcome: Outcome) {
        self.route = route
        self.at = at
        self.outcome = outcome
    }
}

public struct LaneDecision: Sendable {
    public let lane: String
    public let member: String
    /// Одна фраза по-русски. Без объяснения автоматика читается как
    /// своеволие, и первое, что делает человек, — выключает её.
    public let reason: String
    /// Рвать ли живые соединения. При плановой смене — нет, при аварии — да.
    public let cut: Bool

    public init(lane: String, member: String, reason: String, cut: Bool) {
        self.lane = lane
        self.member = member
        self.reason = reason
        self.cut = cut
    }
}

/// Временный решающий слой: держится за живое и уходит с мёртвого.
///
/// Сознательно НЕ реализует ни оценку качества, ни гистерезис, ни выбор по
/// оси — всё это живёт в ядре оркестратора и будет подключено вместо этого
/// класса. Здесь ровно одно правило, которое невозможно применить неверно:
/// если у текущего члена полосы три пробы подряд без ответа, а другой член
/// отвечает — переходим на него. Никаких сравнений «лучше/хуже».
///
/// Такая скупость намеренна: пока движок не подключён, лучше делать заведомо
/// мало, чем изобрести вторую логику выбора, которая потом будет спорить с
/// настоящей.
public actor FailoverOnlyDecider: LaneDecider {

    private struct RouteState {
        var consecutiveFailures = 0
        var lastOK: Date?
        var everSeen = false
    }

    private var states: [String: RouteState] = [:]
    private var lastSwitchAt: [String: Date] = [:]

    /// Порог отказа. Три подряд, а не один: одиночный промах бывает у
    /// исправного маршрута, и переключение по нему — это дребезг.
    private let failureThreshold = 3
    /// Не чаще одного переключения в минуту на полосу — грубая защита от
    /// метаний, пока нет настоящего гистерезиса.
    private let minimumInterval: TimeInterval = 60

    public init() {}

    public func observe(_ sample: RouteSample) async {
        var st = states[sample.route] ?? RouteState()
        switch sample.outcome {
        case .ok:
            st.consecutiveFailures = 0
            st.lastOK = sample.at
            st.everSeen = true
        case .timeout:
            st.consecutiveFailures += 1
            st.everSeen = true
        case .discarded:
            // Ничего не меняем: замер не о маршруте.
            break
        }
        states[sample.route] = st
    }

    public func decide(lanes: [String: [String]], now: Date) async -> [LaneDecision] {
        var out: [LaneDecision] = []
        for (lane, members) in lanes {
            // Полоса без выбора (например, прямой выход) решений не требует.
            let candidates = members.filter { $0 != LaneConfigBuilder.Tag.block }
            guard candidates.count > 1 else { continue }

            guard let current = currentMember(of: lane, among: candidates) else { continue }
            let st = states[current] ?? RouteState()
            guard st.consecutiveFailures >= failureThreshold else { continue }

            if let last = lastSwitchAt[lane], now.timeIntervalSince(last) < minimumInterval {
                continue
            }
            // Берём первого, кто отвечал недавно. Порядок членов задан
            // конфигом и стабилен — предпочтения здесь нет и быть не должно.
            let alive = candidates.first { member in
                guard member != current, let s = states[member] else { return false }
                return s.consecutiveFailures == 0 && s.lastOK != nil
            }
            guard let target = alive else { continue }

            lastSwitchAt[lane] = now
            states[current]?.consecutiveFailures = 0
            out.append(LaneDecision(
                lane: lane,
                member: target,
                reason: "ушли с «\(current)»: \(failureThreshold) пробы подряд без ответа",
                // Мёртвый путь: старые потоки по нему уже никуда не текут.
                cut: true
            ))
        }
        return out
    }

    public func explain() async -> String {
        let seen = states.values.filter(\.everSeen).count
        let dead = states.values.filter { $0.consecutiveFailures >= failureThreshold }.count
        return "измеряется маршрутов: \(seen), не отвечают: \(dead)"
        + " · выбор по качеству пока не подключён"
    }

    // MARK: - Текущий выбор

    private var known: [String: String] = [:]

    /// Управляющий слой сообщает, что ядро выбрало сейчас: сам решающий слой
    /// в сеть не ходит и знать этого не может.
    public func noteCurrent(lane: String, member: String) {
        known[lane] = member
    }

    private func currentMember(of lane: String, among candidates: [String]) -> String? {
        if let k = known[lane], candidates.contains(k) { return k }
        return candidates.first
    }
}
