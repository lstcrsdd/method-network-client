import Foundation
import os.log

/// Настоящий движок выбора вместо временного слоя.
///
/// В движок регистрируются ОСИ ОБХОДА, а не отдельные маршруты. Это не
/// упрощение, а схема из проекта: два яруса автономии.
///
///   полоса (селектор) → ось (urltest) → маршрут
///
/// Внутри оси живого держит само ядро sing-box: у него для этого есть
/// группа с собственными пробами, и она работает, даже если наш контур
/// умрёт. Наш движок решает то, чего ядро не умеет вовсе, — каким СПОСОБОМ
/// проходить. Разница существенная: три сервера на одной оси убивает одна и
/// та же причина, и перебор между ними при блокировке не даёт ничего.
///
/// Поэтому «маршрут» в терминах движка здесь — это ось, и ось у него
/// проставлена настоящая. Классификация отказа по оси, штраф оси и
/// требование «активный и запас на разных осях» работают как задумано.
/// Журнал вынесен из актора: это константа, а не его состояние, и обращаться
/// к ней надо в том числе из инициализатора — до того, как актор начал
/// существовать для других.
private let engineLog = OSLog(subsystem: "network.method.client", category: "Engine")

actor EngineDecider: LaneDecider {

    private let engine: MethodEngine

    /// Тег полосы в конфиге ядра → дескриптор в движке, и обратно.
    private var laneByTag: [String: LaneHandle] = [:]
    private var tagByLane: [LaneHandle: String] = [:]
    /// Тег группы оси → дескриптор, и обратно.
    private var axisByTag: [String: RouteHandle] = [:]
    private var tagByAxis: [RouteHandle: String] = [:]
    /// Ответила ли ось на последнюю пробу. Отдельно от оценки движка:
    /// оценка живёт по восстановленной истории и остаётся положительной
    /// у оси, которая сегодня молчит, — а человеку в «живых способов N»
    /// нужно именно СЕГОДНЯШНЕЕ число.
    private var answered: [String: Bool] = [:]

    /// Полосы, которым мы хоть раз успешно назначили маршрут.
    ///
    /// Нужно, чтобы отличить «движок всё измерил и живых не осталось» от
    /// «движок ещё ничего не измерил». Ответ у него одинаковый — кандидатов
    /// нет, — а смысл противоположный.
    private var everApplied: Set<String> = []

    /// Начало отсчёта. Движок принципиально не читает часы: иначе его решения
    /// перестают быть чистой функцией входа и их нельзя прогнать на записи.
    private let origin = DispatchTime.now()

    // nonisolated: читает только `origin` — константу, заданную при
    // создании. Нужно, чтобы восстановление истории вызывалось из init:
    // обращение к изолированному методу оттуда в Swift 6 станет ошибкой.
    private nonisolated func ms(_ date: Date) -> UInt64 {
        UInt64(max(0, DispatchTime.now().uptimeNanoseconds &- origin.uptimeNanoseconds) / 1_000_000)
    }

    /// Собирает движок под уже собранный план конфига.
    ///
    /// Бросает, если движок отверг описание — например, полосе нужно два
    /// независимых способа пройти, а в наличии один. Молча продолжать в таком
    /// случае нельзя: человек считал бы, что запас есть.
    init(plan: LaneConfigBuilder.Plan, probeIntervalMS: UInt64) throws {
        let engine = try MethodEngine(probeIntervalMs: probeIntervalMS)
        self.engine = engine
        // История восстанавливается ПОСЛЕ регистрации осей и полос: она
        // ссылается на них, и до регистрации ссылаться было бы не на что.
        defer { Self.loadSavedState(into: engine, origin: origin) }

        // Оси. Узел и страна не заполняются: за осью стоит группа маршрутов на
        // разных узлах, и приписать ей один узел значило бы соврать.
        for tag in plan.axes.keys.sorted() {
            guard let key = LaneConfigBuilder.EvasionAxisKey.allCases
                .first(where: { LaneConfigBuilder.Tag.axis($0) == tag }) else { continue }
            // Идентификатор для движка — ЧЕЛОВЕЧЕСКИЙ. Движок вставляет его
            // в свои объяснения дословно, и «ушли с QUIC поверх UDP» человек
            // читает, а «ушли с AX.quic-udp» — нет. Соответствие с тегом
            // ядра держится картами ниже, поэтому имя может быть любым.
            let handle = try engine.addRoute(
                RouteDescriptor(
                    id: key.human,
                    node: "—",
                    transport: key.rawValue,
                    country: "",
                    axis: Self.axis(key),
                    exposure: .tunnelled(),
                    handshakeCost: key == .quicUDP ? .cheap : .expensive,
                    carries: .default
                )
            )
            axisByTag[tag] = handle
            tagByAxis[handle] = tag
        }

        // Полосы. Прямые полосы движку не отдаём вовсе: у них один допустимый
        // исход, выбирать не из чего, и участие движка только добавило бы
        // решений там, где их не бывает.
        for (tag, members) in plan.lanes.sorted(by: { $0.key < $1.key }) {
            let axisMembers = members.filter { $0.hasPrefix("AX.") }
            guard !axisMembers.isEmpty else { continue }
            let handle = try engine.addLane(
                LaneDescriptor(
                    id: tag,
                    title: plan.laneTitles[tag] ?? tag,
                    minAxes: UInt8(min(2, axisMembers.count))
                )
            )
            laneByTag[tag] = handle
            tagByLane[handle] = tag
        }
    }

    // MARK: - LaneDecider

    func observe(_ sample: RouteSample) async {
        guard let route = axisByTag[sample.route] else { return }
        if case .ok = sample.outcome { answered[sample.route] = true }
        if case .timeout = sample.outcome { answered[sample.route] = false }
        let at = ms(sample.at)
        do {
            switch sample.outcome {
            case .ok(let rtt):
                try engine.observe(route, at: at, .ok(rttMs: Float(max(0, rtt))))
            case .timeout:
                try engine.observe(route, at: at, .timeout)
            case .discarded:
                // Недостоверный замер не говорит о маршруте ничего. Движок
                // знает этот случай отдельно и не наказывает за него узел.
                try engine.observe(route, at: at, .discarded(cause: .networkChanged))
            }
        } catch {
            os_log("Движок отверг пробу: %{public}@", log: engineLog, type: .error,
                   String(describing: error))
        }
    }

    func decide(lanes: [String: [String]], now: Date) async -> [LaneDecision] {
        do {
            let decision = try engine.reconcile(nowMs: ms(now))
            var out: [LaneDecision] = []
            // Журнал причин шире списка действий: в нём есть и то, ПОЧЕМУ
            // движок не стал переключаться. Человеку это интереснее прочего,
            // поэтому пишем в системный журнал целиком.
            for reason in decision.reasons {
                os_log("%{public}@", log: engineLog, type: .info, reason.text)
            }
            for action in decision.actions {
                switch action {
                case .select(let lane, let route, let reason):
                    guard let laneTag = tagByLane[lane], let axisTag = tagByAxis[route] else { continue }
                    out.append(LaneDecision(lane: laneTag, member: axisTag,
                                            reason: reason.text, cut: false))
                case .drain(let lane):
                    guard let laneTag = tagByLane[lane] else { continue }
                    // Обрыв приходит отдельным действием — помечаем последнее
                    // решение этой полосы как рвущее.
                    if let i = out.lastIndex(where: { $0.lane == laneTag }) {
                        out[i] = LaneDecision(lane: out[i].lane, member: out[i].member,
                                              reason: out[i].reason, cut: true)
                    }
                case .goEmpty(let lane, _, let reason):
                    guard let laneTag = tagByLane[lane] else { continue }
                    // ХОЛОДНЫЙ СТАРТ — НЕ ОТКАЗ.
                    //
                    // Первые полминуты после подключения движок отвечает
                    // «кандидатов нет» просто потому, что ещё ничего не
                    // измерил: до восьми проб уверенность ниже порога участия
                    // у всех осей сразу. Ответ тот же, что при настоящем
                    // отказе, а смысл противоположный.
                    //
                    // Если перевести это в блокировку, клиент убивает
                    // интернет сразу после подключения — при полностью
                    // исправном туннеле. Так и произошло при первом же живом
                    // прогоне: на экране «Защищено», трафик ноль.
                    //
                    // Поэтому блокируем только ту полосу, которой мы уже
                    // хоть раз назначали маршрут. Пока не назначали — оставляем
                    // выбор ядру: у него внутри оси свои пробы, и до прогрева
                    // движка он справляется лучше, чем наше незнание.
                    guard everApplied.contains(laneTag) else {
                        os_log("Полоса %{public}@: движок ещё греется, выбор оставлен ядру",
                               log: engineLog, type: .info, laneTag)
                        continue
                    }
                    out.append(LaneDecision(lane: laneTag,
                                            member: LaneConfigBuilder.Tag.block,
                                            reason: reason.text, cut: true))
                }
            }
            return out
        } catch {
            os_log("Движок не смог принять решение: %{public}@", log: engineLog, type: .error,
                   String(describing: error))
            return []
        }
    }

    func explain() async -> String {
        guard !answered.isEmpty else { return "измеряем пути обхода…" }
        let alive = answered.values.filter { $0 }.count
        return "живых способов \(alive) из \(axisByTag.count)"
    }

    /// Оси, которые не отвечают. Нужны интерфейсу: молчащая ось — это не
    /// «чуть хуже», а минус целый способ пройти, и человек должен видеть
    /// это до того, как замолчит следующая.
    func silentAxes() async -> [String] {
        answered.filter { !$0.value }.keys.sorted()
    }

    // MARK: - Память между запусками

    /// История измерений переживает перезапуск.
    ///
    /// Без неё каждое подключение начинается с нуля: уверенность добирается
    /// до порога участия примерно за три минуты, и всё это время движок
    /// честно отвечает «кандидатов нет», то есть не решает ничего. С
    /// сохранённой историей первая же успешная проба возвращает уверенность
    /// около 0.6 вместо нуля.
    ///
    /// Моменты времени пересчитываются в нынешнюю эпоху: монотонные часы при
    /// перезапуске обнуляются, и восстановленный момент переключения из
    /// прошлой эпохи означал бы либо вечный запрет менять маршрут, либо
    /// мгновенное разрешение.
    private static var stateURL: URL {
        let base = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Method", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        return base.appendingPathComponent("engine-state.dat")
    }

    private struct Saved: Codable {
        var bytes: [UInt8]
        /// Стенные часы: только чтобы узнать, сколько прошло. Для решений они
        /// не используются — движок читает лишь монотонные.
        var savedAt: Date
    }

    private static func loadSavedState(into engine: MethodEngine, origin: DispatchTime) {
        let ms = { UInt64(max(0, DispatchTime.now().uptimeNanoseconds &- origin.uptimeNanoseconds) / 1_000_000) }
        guard let sealed = try? Data(contentsOf: Self.stateURL),
              let raw = try? SecretStore.open(sealed),
              let saved = try? JSONDecoder().decode(Saved.self, from: raw) else { return }
        let elapsed = UInt64(max(0, Date().timeIntervalSince(saved.savedAt)) * 1000)
        do {
            try engine.loadState(saved.bytes, nowMs: ms(), elapsedMs: elapsed)
            os_log("История измерений восстановлена: прошло %{public}d с",
                   log: engineLog, type: .default, Int(elapsed / 1000))
            // Прочитано прежним ключом: следующая же запись переложит файл на
            // нынешний, отдельного действия не требуется.
        } catch {
            // Битое или несовместимое состояние — не повод падать: начнём с
            // нуля, это лишь медленнее, а не неверно.
            os_log("История измерений не восстановлена: %{public}@",
                   log: engineLog, type: .error, String(describing: error))
            try? FileManager.default.removeItem(at: Self.stateURL)
        }
    }

    /// Сохранить накопленное. Зовётся при остановке контура.
    func saveState() async {
        guard let bytes = try? engine.saveState(nowMs: ms(Date())) else { return }
        let saved = Saved(bytes: bytes, savedAt: Date())
        guard let raw = try? JSONEncoder().encode(saved),
              let sealed = try? SecretStore.seal(raw) else { return }
        // Шифруем: в истории лежат идентификаторы маршрутов и отпечаток сети,
        // то есть след того, где человек бывал.
        try? sealed.write(to: Self.stateURL, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                               ofItemAtPath: Self.stateURL.path)
    }

    // MARK: - Подтверждение применения

    /// Исполнитель обязан подтвердить, что переключение состоялось.
    ///
    /// Между «движок решил» и «ядро переключило» лежит запрос, который может
    /// не дойти. Если считать полосу переключённой сразу, движок замолчит
    /// ровно тогда, когда переключение не состоялось.
    func noteApplied(lane: String, member: String) async {
        guard let l = laneByTag[lane], let r = axisByTag[member] else { return }
        try? engine.laneApplied(l, route: r)
        everApplied.insert(lane)
    }

    /// Оценка оси под класс нагрузки — для интерфейса.
    func score(axisTag: String, sla: SLA) async -> Float? {
        guard let h = axisByTag[axisTag] else { return nil }
        return (try? engine.score(h, sla: sla, nowMs: ms(Date())))?.value
    }

    private static func axis(_ key: LaneConfigBuilder.EvasionAxisKey) -> Axis {
        switch key {
        case .quicUDP:    return .quicUDP
        case .fakeTLSH2:  return .fakeTLSOverH2
        case .fakeTLSTCP: return .fakeTLSOverTCP
        case .realTLS:    return .realTLS
        case .rawStream:  return .rawStream
        }
    }
}
