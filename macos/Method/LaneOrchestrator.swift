import Foundation
import os.log

/// Управляющий контур: измеряет маршруты и меняет привязку полос на живом ядре.
///
/// Работает поверх уже поднятого туннеля и НИКОГДА его не пересоздаёт. Это
/// главное отличие от прежнего поведения: раньше смена сервера означала
/// остановку ядра и подъём заново — обрыв всех соединений и провал интернета
/// на несколько секунд. Здесь смена маршрута — один запрос к локальному API,
/// и человек её не замечает.
///
/// Контур сознательно НЕ умеет переподключаться, перезапускать ядро и
/// выбирать сервер при старте. Всем этим занимается контроллер со своим
/// сторожем и лестницей восстановления, и два механизма, независимо решающих
/// «переподключиться», дали бы ровно тот дребезг, от которого мы защищаемся.
@MainActor
final class LaneOrchestrator: ObservableObject {

    /// Что показать человеку. Без объяснения автоматика читается как
    /// своеволие, и первое, что делает человек, — выключает её.
    @Published private(set) var lastReason: String?
    /// Когда объяснение появилось.
    ///
    /// Объяснение ЖИВЁТ НЕДОЛГО: оно описывает событие («ушли с такого-то
    /// пути»), а не состояние. Провисев на экране полчаса, оно превращается в
    /// неправду — событие давно прошло, а строка утверждает настоящее время.
    /// Текущее состояние показывает строка пути, дублировать его незачем.
    private var reasonAt: Date?
    /// Сколько держим объяснение на экране.
    private let reasonLifetime: TimeInterval = 30
    /// Полосы, которым уже объявляли решение.
    ///
    /// ПЕРВЫЙ выбор человеку не сообщается вовсе. Он не несёт сведений:
    /// подключение обязано куда-то пойти, и сообщение «включаем такой-то
    /// путь» отвечает на вопрос, которого никто не задавал. Смысл объяснения
    /// в другом — сказать, почему путь СМЕНИЛСЯ сам, без спроса. Об этом и
    /// говорим, начиная со второго решения.
    private var announced: Set<String> = []
    /// Кому сообщить, что маршрут сменился: адрес выхода надо перемерить,
    /// иначе в интерфейсе останется адрес прежнего узла.
    var onRouteChanged: (() -> Void)?
    @Published private(set) var laneBindings: [String: String] = [:]
    @Published private(set) var routeDelays: [String: Int] = [:]
    @Published private(set) var isRunning = false
    /// Узел, который несёт трафик прямо сейчас.
    ///
    /// Отличается от выбранного человеком: он выбирает точку старта, а дальше
    /// путь ведёт движок. Показывать в шапке выбранный, когда трафик идёт
    /// через другой узел, — значит врать на главном экране: там окажется
    /// «Литва · Hysteria2» при финском адресе в соседнем поле.
    @Published private(set) var activeProfile: ServerProfile?

    private let log = OSLog(subsystem: "network.method.client", category: "Orchestrator")

    private var runtime: LaneRuntime?
    private var decider: LaneDecider?
    /// Настоящий движок, если он собрался. Нужен отдельно от протокола ради
    /// подтверждения применения — временный слой этого не требует.
    private var engine: EngineDecider?
    private var plan: LaneConfigBuilder.Plan?
    private var loop: Task<Void, Never>?
    /// Круги с последнего сохранения истории. Сохранять каждый круг незачем,
    /// терять её при падении — жалко: раз в минуту разумный размен.
    private var ticksSinceSave = 0

    /// Шаг цикла. Значение обязано совпадать с тем, что объявлено движку:
    /// он считает от него возраст замеров, и расхождение делает уверенность
    /// вечно недостаточной.
    ///
    /// Держится он РОВНЫМ — цикл спит до следующей отметки, а не «столько-то
    /// после работы». Разница не косметическая: пока сон шёл после круга, а
    /// молчащие оси выбирали весь тайм-аут, круг растягивался до тринадцати
    /// секунд при объявленных шести. Движок считает возраст замеров от
    /// объявленного шага, и при вдвое большем настоящем уверенность не
    /// дорастала до порога участия — он честно отвечал «кандидатов нет» и
    /// не решал ничего.
    private let interval: Duration = .seconds(6)
    private let declaredIntervalMS: UInt64 = 6000
    /// Шаг прогрева. Пока движок не принял ни одного решения, замеры нужны
    /// чаще: порог участия в выборе он берёт на пятидесятой пробе (измерено
    /// `Core/method-core/examples/warmup.rs`), и при шаге в шесть секунд это
    /// пять минут, всё это время выбор остаётся за ядром. При двух секундах —
    /// меньше двух минут.
    ///
    /// Расхождение с объявленным шагом здесь безвредно: движок считает от
    /// него только возраст замеров, а замер мы отдаём сразу после пробы,
    /// то есть нулевого возраста. Обратное расхождение — реальный шаг БОЛЬШЕ
    /// объявленного — как раз и держало движок вечно непрогретым.
    private let warmupInterval: Duration = .seconds(2)
    /// Потолок прогрева. Если движок так и не решил ничего (скажем, живых
    /// осей просто нет), частить пробами бесконечно незачем.
    private let warmupTickLimit = 150
    /// Сколько проб понадобится движку — для честной строки в интерфейсе.
    private let warmupNeeded = 50
    private var ticks = 0
    /// Движок принял хотя бы одно решение: прогрев закончен.
    private var engineDecided = false
    /// Тайм-аут пробы. Заведомо меньше шага: иначе одна молчащая ось
    /// растягивает круг и снова разъезжается с объявленным шагом.
    private let probeTimeoutMS = 4000
    /// Адрес проб. Только https: ядро игнорирует http и молча подставляет
    /// собственный адрес на gstatic — измерялся бы Google, а не наш маршрут.
    private let probeURL = "https://www.gstatic.com/generate_204"

    // MARK: - Жизненный цикл

    func start(plan: LaneConfigBuilder.Plan, endpoint: LaneRuntime.Endpoint) {
        stop()
        self.plan = plan
        let rt = LaneRuntime(endpoint: endpoint)
        runtime = rt

        // Настоящий движок, если собрался. Он может отказать по делу — скажем,
        // полосе нужно два независимых способа пройти, а в наличии один. Тогда
        // остаётся временный слой: он не выбирает по качеству, но уводит с
        // мёртвого пути, и это лучше, чем ничего.
        if let real = try? EngineDecider(plan: plan, probeIntervalMS: declaredIntervalMS) {
            engine = real
            decider = real
            os_log("Контур полос запущен на движке: полос %d, осей %d",
                   log: log, type: .default, plan.lanes.count, plan.axes.count)
        } else {
            engine = nil
            decider = FailoverOnlyDecider()
            os_log("Движок не принял описание — работает временный слой",
                   log: log, type: .error)
        }
        isRunning = true

        loop = Task { [weak self] in
            guard let self else { return }
            let clock = ContinuousClock()
            var next = clock.now
            while !Task.isCancelled {
                await self.tick()
                let step = self.stepNow
                next = next.advanced(by: step)
                // Отстали больше, чем на шаг (машина спала, круг затянулся) —
                // не догоняем очередью пропущенных кругов, а берём отсчёт
                // заново: пачка проб подряд ничего не измеряет, только шумит.
                if next < clock.now { next = clock.now.advanced(by: step) }
                try? await clock.sleep(until: next)
            }
        }
    }

    func stop() {
        // Сохраняем накопленное ДО того, как отпустим движок: иначе следующее
        // подключение снова начнётся с трёх минут незнания.
        if let engine {
            Task { await engine.saveState() }
        }
        loop?.cancel()
        loop = nil
        runtime = nil
        decider = nil
        engine = nil
        plan = nil
        isRunning = false
        laneBindings = [:]
        routeDelays = [:]
        activeProfile = nil
        reasonAt = nil
        announced.removeAll()
        lastReason = nil
    }

    /// Шаг текущего круга: частый на прогреве, ровный после.
    private var stepNow: Duration {
        (engineDecided || ticks >= warmupTickLimit) ? interval : warmupInterval
    }

    // MARK: - Один круг

    private func tick() async {
        // Просроченное объяснение убираем в начале круга: тогда оно исчезает
        // само, без отдельного таймера и без гонки с новым решением.
        if let at = reasonAt, Date().timeIntervalSince(at) > reasonLifetime {
            lastReason = nil
            reasonAt = nil
        }
        guard let rt = runtime, let dec = decider, let plan else { return }

        // 1. Что ядро выбрало сейчас. Сам решающий слой в сеть не ходит и
        //    знать этого не может.
        let lanes: [String: LaneRuntime.LaneState]
        do {
            lanes = try await rt.lanes()
        } catch {
            // Ядро молчит. Это НЕ отказ маршрутов: если записать его как
            // отказ, движок решит, что умерли все узлы разом, и начнёт
            // метаться по исправной сети. Молчим и ждём следующего круга —
            // ядром занимается сторож контроллера, а не мы.
            os_log("Ядро не отвечает, круг пропущен: %{public}@",
                   log: log, type: .info, error.localizedDescription)
            return
        }

        var bindings: [String: String] = [:]
        for (tag, st) in lanes where tag.hasPrefix("L.") {
            bindings[tag] = st.now
            if let simple = dec as? FailoverOnlyDecider {
                await simple.noteCurrent(lane: tag, member: st.now)
            }
        }
        laneBindings = bindings

        // Полоса указывает на ось, ось — на конкретный маршрут. Два шага, и
        // оба обязательны: имя оси человеку не говорит, через какой узел он
        // вышел.
        if let axisTag = lanes[plan.defaultLaneTag]?.now, axisTag.hasPrefix("AX."),
           let routeTag = lanes[axisTag]?.now {
            activeProfile = plan.routes[routeTag]
        } else {
            activeProfile = nil
        }

        // 2. Меряем. Пробуем оси, а не каждый маршрут: внутри оси ядро само
        //    держит живого, а нам важно, жив ли СПОСОБ пройти.
        // Оси опрашиваются ПАРАЛЛЕЛЬНО. Последовательный опрос растягивал круг
        // до двенадцати секунд при заявленном шаге в пять, а движок считает
        // уверенность от заявленного: при вдвое большем реальном интервале
        // она никогда не дорастала до порога участия, и движок вечно
        // «грелся», ничего не решая.
        let axes = lanes.keys.sorted().filter { $0.hasPrefix("AX.") }
        var delays: [String: Int] = [:]
        await withTaskGroup(of: (String, Int?).self) { group in
            for tag in axes {
                group.addTask { (tag, try? await rt.delay(route: tag, url: self.probeURL,
                                                          timeoutMS: self.probeTimeoutMS)) }
            }
            for await (tag, ms) in group {
                if let ms { delays[tag] = ms }
            }
        }
        if Task.isCancelled { return }
        let at = Date()
        for tag in axes {
            let ms = delays[tag]
            await dec.observe(RouteSample(route: tag, at: at,
                                          outcome: ms.map { .ok(rttMS: $0) } ?? .timeout))
        }
        routeDelays = delays

        // Замеры пишем в журнал. Без этого «движок не принимает решений»
        // неотличимо от «пробы не долетают»: и то и другое выглядит как
        // молчание, а причины у них противоположные.
        if delays.isEmpty {
            os_log("Пробы: ни одна ось не ответила (осей %d)",
                   log: log, type: .error, lanes.keys.filter { $0.hasPrefix("AX.") }.count)
        } else {
            let text = delays.sorted { $0.key < $1.key }
                .map { "\($0.key)=\($0.value)мс" }.joined(separator: " ")
            os_log("Пробы: %{public}@", log: log, type: .info, text)
            // Молчащие оси называем поимённо. Это не мелочь: молчащая ось —
            // минус целый способ пройти, и без строки в журнале она выглядит
            // так же, как ось, которой просто нет.
            let silent = axes.filter { delays[$0] == nil }
            if !silent.isEmpty {
                os_log("Не отвечают оси: %{public}@ — проверь их маршруты, для полосы их всё равно что нет",
                       log: log, type: .error, silent.joined(separator: ", "))
            }
        }

        // Периодическое сохранение истории: только при остановке её потеряло
        // бы любое падение или снятие процесса, а это снова три минуты
        // незнания при следующем подключении.
        ticks += 1
        ticksSinceSave += 1
        if ticksSinceSave >= 12, let engine {
            ticksSinceSave = 0
            await engine.saveState()
        }

        // 3. Решаем и применяем.
        let laneMembers = Dictionary(uniqueKeysWithValues:
            lanes.filter { $0.key.hasPrefix("L.") }.map { ($0.key, $0.value.members) })
        let decisions = await dec.decide(lanes: laneMembers, now: Date())

        // Пока движок не решает, человеку говорим прямо, чем он занят и
        // сколько это ещё продлится. Молчащая автоматика читается как
        // сломанная, а «изучаем пути» — как работа.
        if !engineDecided && decisions.isEmpty {
            lastReason = "изучаем пути обхода: \(min(ticks, warmupNeeded)) замеров из \(warmupNeeded)"
            reasonAt = Date()
        }

        for d in decisions {
            if Task.isCancelled { return }
            do {
                try await rt.select(lane: d.lane, member: d.member)
                if d.cut {
                    // Мёртвый путь: старые потоки по нему уже никуда не текут,
                    // держать их — значит держать человека без интернета.
                    _ = try? await rt.drain(lane: d.lane)
                }
                // Подтверждаем движку, что переключение состоялось. Между
                // «решил» и «ядро переключило» лежит запрос, который может не
                // дойти; без подтверждения движок замолчал бы ровно тогда,
                // когда переключение не произошло.
                await engine?.noteApplied(lane: d.lane, member: d.member)
                engineDecided = true
                if announced.contains(d.lane) {
                    lastReason = d.reason.isEmpty ? humanize(d) : d.reason
                    reasonAt = Date()
                } else {
                    announced.insert(d.lane)
                }
                onRouteChanged?()
                os_log("Полоса %{public}@ -> %{public}@: %{public}@",
                       log: log, type: .default, d.lane, d.member, d.reason)
            } catch {
                // Ядро отвергло — например, увод за пределы селектора. Это
                // защита, а не сбой: сообщаем и не настаиваем.
                os_log("Отказ применить решение: %{public}@",
                       log: log, type: .error, error.localizedDescription)
            }
        }
    }

    /// Технические теги в человеческую фразу. `AX.real-tls` человеку ничего
    /// не говорит, а «настоящий TLS» — говорит.
    private func humanize(_ d: LaneDecision) -> String {
        let axis = LaneConfigBuilder.EvasionAxisKey.allCases
            .first { LaneConfigBuilder.Tag.axis($0) == d.member }
        let target = axis.map(humanAxis) ?? d.member
        return "Переключились на \(target): \(d.reason)"
    }

    private func humanAxis(_ axis: LaneConfigBuilder.EvasionAxisKey) -> String {
        switch axis {
        case .quicUDP:     return "QUIC поверх UDP"
        case .fakeTLSH2:   return "gRPC в поддельном TLS"
        case .fakeTLSTCP:  return "TCP в поддельном TLS"
        case .realTLS:     return "настоящий TLS"
        case .rawStream:   return "поток без рукопожатия"
        }
    }
}
