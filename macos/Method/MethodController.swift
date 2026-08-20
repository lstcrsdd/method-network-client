import Combine
import Foundation

enum ConnState: Equatable {
    case disconnected
    case connecting
    case connected
    case error(String)
}

/// Один исход на континуацию.
///
/// Возобновить `CheckedContinuation` второй раз — это не ошибка и не исключение,
/// а немедленное падение процесса. А XPC вполне способен отдать и ответ, и
/// обрыв соединения: обработчик прерывания живёт отдельно от reply-блока.
final class OnceFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var fired = false
    func fire() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if fired { return false }
        fired = true
        return true
    }
}

/// Итог разбора вставленного текста (ссылки/подписки вперемешку, по строке).
struct ImportOutcome {
    var addedProfiles = 0
    var addedSubscriptions = 0
    var errors: [String] = []
}

/// Машина состояний подключения + хранилище конфигов/подписок для Method.
/// Единый источник правды для UI.
@MainActor
final class MethodController: ObservableObject {
    static let shared = MethodController()

    // Конфиги (ручные + из подписок) и сами подписки.
    @Published private(set) var storedProfiles: [StoredProfile] = []
    @Published private(set) var subscriptions: [Subscription] = []
    @Published private(set) var refreshingSubscriptionIDs: Set<UUID> = []
    @Published var selectedID: UUID?

    @Published private(set) var state: ConnState = .disconnected
    @Published private(set) var isFailingOver = false
    /// Управляющий контур: меняет привязку полос на живом ядре, не трогая
    /// туннель. Отдельный объект, а не метод контроллера, потому что у него
    /// свой цикл и своё состояние, а контроллер и так велик.
    let orchestrator = LaneOrchestrator()
    /// Пользовательские сценарии. Заводские, пока человек не написал своих.
    @Published private(set) var policy: LanePolicy = PolicyStore.load()
    /// Прогресс автоперебора при подключении, напр. "Пробуем 2 из 10…" (nil — не идёт).
    @Published private(set) var connectAttemptStatus: String?
    @Published private(set) var sessionSeconds: Int = 0
    @Published private(set) var exitIP: String?
    @Published private(set) var pings: [UUID: Int] = [:]
    @Published private(set) var isMeasuringPings = false
    @Published var autoReconnect = true
    /// Блокировать трафик мимо туннеля.
    ///
    /// По умолчанию ВЫКЛЮЧЕНО, и это временно. Первая же попытка включить его
    /// по умолчанию лишила интернета все узлы разом: правила не разрешали
    /// резолвер, без которого ядро не узнаёт адрес узла, а доменные адреса
    /// уходили в пакетный фильтр как есть — он принимает только литералы.
    /// Обе ошибки исправлены, но остаётся третья, нерешённая: домены Trojan
    /// живут за Cloudflare, его адреса меняются, и закреплённый при
    /// подключении набор однажды перестанет совпадать. Защита откажет в
    /// правильную сторону — заблокирует, а не выпустит, — но человек
    /// останется без интернета и не поймёт почему.
    ///
    /// Пока это не решено, умолчание обязано быть безопасным для
    /// работоспособности, а не для приватности: неработающий клиент выключают
    /// целиком, вместе с защитой.
    @Published var killSwitchEnabled: Bool = UserDefaults.standard.object(
        forKey: "killSwitch") as? Bool ?? false {
        didSet { UserDefaults.standard.set(killSwitchEnabled, forKey: "killSwitch") }
    }

    /// Часы между авто-обновлениями подписки. 0 — выключено.
    @Published var autoRefreshIntervalHours: Double {
        didSet { UserDefaults.standard.set(autoRefreshIntervalHours, forKey: Self.autoRefreshKey) }
    }
    private static let autoRefreshKey = "method.subscriptions.autoRefreshHours"

    // Живой трафик (Clash API).
    @Published private(set) var downloadSpeed: Double = 0
    @Published private(set) var uploadSpeed: Double = 0
    @Published private(set) var downloadHistory: [Double] = []
    private static let historyLength = 40

    private let daemon = MethodDaemon.shared
    private let reconnectManager = ReconnectManager()
    private let connectivityMonitor = ConnectivityMonitor()
    private var failoverCooldown: [UUID: Date] = [:]

    private var sessionTimer: Timer?
    private var sessionStartedAt: Date?
    private var trafficTimer: Timer?
    private var lastTraffic: ClashAPIClient.TrafficSnapshot?
    private var connectTask: Task<Void, Never>?
    private var recoveryTask: Task<Void, Never>?
    /// Номер восстановления. Нужен ровно для одного: снимать `recoveryTask`
    /// имеет право только то восстановление, которое его и поставило.
    /// Иначе хвост отменённой попытки обнулял ссылку уже на СЛЕДУЮЩУЮ, и
    /// сторож ядра спокойно запускал вторую лестницу поверх первой — два
    /// перезапуска ядра и две смены сервера навстречу друг другу.
    private var recoveryGeneration = 0
    /// Переподключение после пробуждения уже идёт. Пробуждение приходит двумя
    /// уведомлениями сразу (`didWake` + `screensDidWake`), и без флага ядро
    /// перезапускалось дважды подряд, второй раз — поверх едва поднявшегося.
    private var reconnectInFlight = false
    private var coreWatchdogTimer: Timer?
    private var autoRefreshTimer: Timer?
    private static let staleCheckInterval: TimeInterval = 900
    /// Сколько серверов максимум перебрать за один Connect (ограничивает общее время).
    private static let maxFallbackCandidates = 6
    // Сколько ждём сквозной связности после старта процесса — в ConnectivityMonitor
    // (там же, где эта же проверка идёт по таймеру: два места с разными сроками
    // разъезжаются).

    // MARK: - Живучесть

    /// Поколение подключения. Каждый Connect/Disconnect увеличивает счётчик, и
    /// поздний ответ от предыдущей попытки применяется к UI только если
    /// поколение не сменилось. Без этого ответ на старый `stopTunnel`
    /// прилетал уже после нового `connect()` и переводил экран в «Не
    /// подключено» поверх живого туннеля — то есть врал ровно наоборот.
    private var connectionEpoch = 0

    /// Сколько раз подряд перезапускали ядро на ТЕКУЩЕМ сервере.
    private var coreRestartsForCurrentServer = 0
    private static let maxCoreRestartsPerServer = 2
    /// Отметки всех восстановлений — и перезапусков ядра, и смен сервера.
    /// Лимит за окно нужен, чтобы не крутить молчаливый вечный цикл
    /// перезапусков: после исчерпания — честная ошибка и реальное отключение.
    private var recoveryAttempts: [Date] = []
    private static let maxRecoveriesInWindow = 5
    private static let recoveryWindow: TimeInterval = 600
    /// Карантин сервера, который только что подвёл.
    private static let failoverCooldownSeconds: TimeInterval = 90
    /// Как часто спрашиваем демон, жив ли процесс ядра.
    private static let coreWatchdogInterval: TimeInterval = 6
    /// Срок на ответ демона по каждой команде (в XPC-обёртке).
    private static let daemonTimeout: TimeInterval = 30
    private static let daemonQuickTimeout: TimeInterval = 8

    /// Сколько строк максимум разбираем за один импорт и сколько серверов
    /// максимум пингуем. Подписка приходит с чужого сервера: пятьдесят тысяч
    /// строк — это пятьдесят тысяч процессов `ping` и гарантированное
    /// «приложение не отвечает».
    private static let maxImportLines = 500
    private static let maxPingTargets = 60
    private static let maxPingConcurrency = 8
    private static let maxProfilesPerSubscription = 500
    /// Секрет берётся из связки ключей и случаен на установку. Константа в
    /// исходниках давала право управлять ядром и читать список соединений
    /// любому процессу под тем же пользователем.
    private static let singBoxOptions = SingBoxConfigBuilder.Options(
        clashController: "127.0.0.1:9190",
        clashSecret: SecretStore.clashSecret()
    )

    var isConnected: Bool { state == .connected }
    var daemonReady: Bool { daemon.isRunningFromApplications }

    var selected: ServerProfile? {
        guard let id = selectedID else { return storedProfiles.first?.profile }
        return (storedProfiles.first { $0.id == id } ?? storedProfiles.first)?.profile
    }
    func isSelected(_ id: UUID) -> Bool { selected?.id == id }

    /// Профили без подписки (добавлены вручную ссылкой).
    var manualProfiles: [StoredProfile] { storedProfiles.filter { $0.subscriptionID == nil } }
    func profiles(for subscriptionID: UUID) -> [StoredProfile] {
        storedProfiles.filter { $0.subscriptionID == subscriptionID }
    }

    init() {
        // Хранилище — тоже внешние данные: туда могла попасть запись из старой
        // версии, когда порт не проверялся. Чистим на входе, иначе негодный
        // профиль будет ронять запуск снова и снова.
        let loadedProfiles = ProfileStorage.loadProfiles().filter { Self.isUsable($0.profile) }
        storedProfiles = loadedProfiles
        subscriptions = ProfileStorage.loadSubscriptions()
        selectedID = loadedProfiles.first?.id
        let storedInterval = UserDefaults.standard.object(forKey: Self.autoRefreshKey) as? Double
        autoRefreshIntervalHours = storedInterval ?? 6
        reconnectManager.onReconnect = { [weak self] in
            Task { @MainActor in self?.handleAutoReconnect() }
        }
        connectivityMonitor.onUnhealthy = { [weak self] in
            Task { @MainActor in self?.handleUnhealthyConnection() }
        }
    }

    /// Вызывается один раз при появлении главного окна.
    func bootstrap() {
        daemon.refresh()
        measurePings()
        startAutoRefreshScheduler()
        Task { await refreshStaleSubscriptions() }
        Task { await reconcileWithDaemon() }
    }

    /// Туннель поднимает root-демон и живёт он дольше приложения: закрыли окно
    /// или приложение упало — трафик по-прежнему идёт через туннель. Экран при
    /// этом показывал «Не подключено», то есть врал, и отключиться было нечем.
    /// Спрашиваем демон и показываем то, что есть на самом деле.
    private func reconcileWithDaemon() async {
        guard daemonReady, state == .disconnected else { return }
        guard let running = await isTunnelRunningAwait(), running else { return }
        guard state == .disconnected else { return }
        state = .connected
        // Время сессии считается от этого момента: когда туннель поднялся на
        // самом деле, знает только демон, а такого вопроса в протоколе нет.
        startSessionTimer()
        startTrafficPolling()
        refreshExitIP()
        connectivityMonitor.start()
        startCoreWatchdog()
        if autoReconnect { reconnectManager.start() }

        // Туннель поднят прошлым запуском приложения, и плана у нас нет:
        // подхватить чужой конфиг мы не можем, а значит и управлять полосами
        // тоже. Молчать об этом нельзя — человек видел бы «Защищено» и думал,
        // что выбор пути работает, хотя контур не запущен.
        //
        // Не переподключаемся сами: рвать живой туннель при запуске
        // приложения хуже, чем работать без выбора пути. Говорим и оставляем
        // решение человеку.
        adoptedWithoutPlan = true
    }

    /// Туннель достался от прошлого запуска: полосами не управляем.
    @Published private(set) var adoptedWithoutPlan = false

    func prepareDaemonIfNeeded() { daemon.refresh() }

    // MARK: - Импорт (ссылки и/или подписки, вперемешку по строкам)

    func addFromInput(_ raw: String) async -> ImportOutcome {
        var outcome = ImportOutcome()
        var lines = raw.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }

        // Вставить в поле можно что угодно, включая мегабайтный файл. Разбор
        // идёт в главном потоке, поэтому предел обязателен: иначе приложение
        // замирает намертво и выглядит как падение.
        if lines.count > Self.maxImportLines {
            outcome.errors.append(
                "Слишком много строк (\(lines.count)). Разобрали первые \(Self.maxImportLines)."
            )
            lines = Array(lines.prefix(Self.maxImportLines))
        }

        for (i, line) in lines.enumerated() {
            if let url = URL(string: line), let scheme = url.scheme?.lowercased(),
               scheme == "http" || scheme == "https" {
                do {
                    try await addSubscription(url: url)
                    outcome.addedSubscriptions += 1
                } catch {
                    outcome.errors.append("строка \(i + 1): \(error.localizedDescription)")
                }
            } else {
                do {
                    let profile = try ShareLinkParser.parse(line)
                    guard Self.isUsable(profile) else {
                        outcome.errors.append(
                            "строка \(i + 1): недопустимый адрес или порт (\(profile.host):\(profile.port))"
                        )
                        continue
                    }
                    if addProfileIfNew(profile, subscriptionID: nil) { outcome.addedProfiles += 1 }
                } catch {
                    outcome.errors.append("строка \(i + 1): \(error.localizedDescription)")
                }
            }
        }
        if outcome.addedProfiles > 0 { measurePings() }
        return outcome
    }

    /// Профиль пригоден к использованию.
    ///
    /// Порт приходит из чужих рук — из ссылки, из подписки, из JSON панели — и
    /// нигде по дороге не проверяется на диапазон: `URLComponents` спокойно
    /// отдаёт `port = 999999`, `Int("999999")` тоже. Дальше по коду такой порт
    /// превращался в `UInt16` и ронял приложение прямо на старте (пинги
    /// считаются в `bootstrap()`), причём при каждом запуске — профиль-то
    /// сохранён. Отсекаем на входе.
    static func isUsable(_ profile: ServerProfile) -> Bool {
        guard !profile.host.trimmingCharacters(in: .whitespaces).isEmpty else { return false }
        // Хост уходит аргументом в /sbin/ping — строка, начинающаяся с дефиса,
        // была бы разобрана как ключ.
        guard !profile.host.hasPrefix("-") else { return false }
        guard profile.port > 0, profile.port <= 65535 else { return false }
        return true
    }

    @discardableResult
    private func addProfileIfNew(_ profile: ServerProfile, subscriptionID: UUID?) -> Bool {
        guard Self.isUsable(profile) else { return false }
        let dup = storedProfiles.contains { key(for: $0.profile) == key(for: profile) }
        guard !dup else { return false }
        storedProfiles.append(StoredProfile(profile: profile, subscriptionID: subscriptionID))
        if selectedID == nil { selectedID = profile.id }
        ProfileStorage.saveProfiles(storedProfiles)
        return true
    }

    func removeProfile(_ id: UUID) {
        storedProfiles.removeAll { $0.id == id }
        if selectedID == id { selectedID = storedProfiles.first?.id }
        ProfileStorage.saveProfiles(storedProfiles)
    }

    private func key(for profile: ServerProfile) -> String {
        "\(profile.host):\(profile.port):\(profile.protocol.rawValue)"
    }

    // MARK: - Подписки

    func addSubscription(url: URL) async throws {
        let result = try await SubscriptionService.fetch(url)
        var sub = Subscription(url: url, name: result.suggestedName ?? url.host ?? "Подписка")
        sub.lastUpdatedAt = Date()
        sub.trafficUsed = result.trafficUsed
        sub.trafficTotal = result.trafficTotal
        sub.expiresAt = result.expiresAt
        sub.lastSkippedCount = result.skippedCount
        sub.title = result.title
        sub.announce = result.announce
        sub.updateIntervalHours = result.updateIntervalHours
        sub.webPageURL = result.webPageURL
        sub.supportURL = result.supportURL
        subscriptions.append(sub)
        let dropped = mergeSubscriptionProfiles(subscriptionID: sub.id, newProfiles: result.profiles)
        if dropped > 0, let idx = subscriptions.firstIndex(where: { $0.id == sub.id }) {
            subscriptions[idx].lastSkippedCount = result.skippedCount + dropped
        }
        ProfileStorage.saveSubscriptions(subscriptions)
    }

    func refreshSubscription(_ id: UUID) async {
        guard let startIndex = subscriptions.firstIndex(where: { $0.id == id }) else { return }
        // Тот же id уже обновляется (кнопка + таймер автообновления) — второй
        // заход только продублировал бы сетевой запрос и сбил бы индикатор.
        guard !refreshingSubscriptionIDs.contains(id) else { return }
        let url = subscriptions[startIndex].url

        refreshingSubscriptionIDs.insert(id)
        defer { refreshingSubscriptionIDs.remove(id) }

        let fetched: Result<SubscriptionService.FetchResult, Error>
        do { fetched = .success(try await SubscriptionService.fetch(url)) }
        catch { fetched = .failure(error) }

        // ВАЖНО: индекс ищем заново. Между `await` и этой строкой человек мог
        // удалить любую подписку — сохранённый индекс уехал бы за границу
        // массива, и приложение упало бы с «Index out of range» ровно в тот
        // момент, когда подписка догрузилась.
        guard let idx = subscriptions.firstIndex(where: { $0.id == id }) else { return }

        switch fetched {
        case .success(let result):
            subscriptions[idx].lastUpdatedAt = Date()
            subscriptions[idx].lastError = nil
            subscriptions[idx].trafficUsed = result.trafficUsed
            subscriptions[idx].trafficTotal = result.trafficTotal
            subscriptions[idx].expiresAt = result.expiresAt
            subscriptions[idx].title = result.title
            subscriptions[idx].announce = result.announce
            subscriptions[idx].updateIntervalHours = result.updateIntervalHours
            subscriptions[idx].webPageURL = result.webPageURL
            subscriptions[idx].supportURL = result.supportURL
            let dropped = mergeSubscriptionProfiles(subscriptionID: id, newProfiles: result.profiles)
            // Отброшенные из-за негодных адресов считаем вместе с теми, что не
            // разобрал парсер: человек должен видеть, что пришло не всё.
            subscriptions[idx].lastSkippedCount = result.skippedCount + dropped
        case .failure(let error):
            subscriptions[idx].lastError = error.localizedDescription
        }
        ProfileStorage.saveSubscriptions(subscriptions)
    }

    func refreshAllSubscriptions() async {
        for id in subscriptions.map(\.id) { await refreshSubscription(id) }
    }

    func removeSubscription(_ id: UUID) {
        let hadSelected = storedProfiles.first { $0.id == selectedID }?.subscriptionID == id
        subscriptions.removeAll { $0.id == id }
        storedProfiles.removeAll { $0.subscriptionID == id }
        if hadSelected { selectedID = storedProfiles.first?.id }
        ProfileStorage.saveSubscriptions(subscriptions)
        ProfileStorage.saveProfiles(storedProfiles)
    }

    /// Заменяет профили этой подписки новыми; старается сохранить выбор того же
    /// логического сервера (совпадение host:port:protocol), т.к. UUID при каждом
    /// разборе генерируется заново.
    /// Возвращает число профилей, отброшенных как непригодные.
    @discardableResult
    private func mergeSubscriptionProfiles(subscriptionID: UUID, newProfiles: [ServerProfile]) -> Int {
        let filtered = newProfiles.filter(Self.isUsable)
        // Предел на подписку. Проверка злыми данными показала: десять тысяч
        // ссылок разбираются успешно и все попадают в хранилище и в список —
        // после чего интерфейс можно закрывать.
        let usable = Array(filtered.prefix(Self.maxProfilesPerSubscription))
        let dropped = newProfiles.count - usable.count
        let previousKey = selected.map(key(for:))
        storedProfiles.removeAll { $0.subscriptionID == subscriptionID }
        let added = usable.map { StoredProfile(profile: $0, subscriptionID: subscriptionID) }
        storedProfiles.append(contentsOf: added)
        ProfileStorage.saveProfiles(storedProfiles)

        if let previousKey, let match = added.first(where: { key(for: $0.profile) == previousKey }) {
            selectedID = match.id
        } else if selectedID == nil || !storedProfiles.contains(where: { $0.id == selectedID }) {
            selectedID = storedProfiles.first?.id
        }
        measurePings()
        return dropped
    }

    private func startAutoRefreshScheduler() {
        autoRefreshTimer?.invalidate()
        let t = Timer(timeInterval: Self.staleCheckInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refreshStaleSubscriptions() }
        }
        RunLoop.main.add(t, forMode: .common)
        autoRefreshTimer = t
    }

    private func refreshStaleSubscriptions() async {
        guard autoRefreshIntervalHours > 0 else { return }
        let interval = autoRefreshIntervalHours * 3600
        let now = Date()
        for sub in subscriptions {
            let stale = sub.lastUpdatedAt.map { now.timeIntervalSince($0) > interval } ?? true
            if stale { await refreshSubscription(sub.id) }
        }
    }

    // MARK: - Подключение

    func toggleConnection() {
        switch state {
        case .connected, .connecting: disconnect()
        default: connect()
        }
    }

    func connect() { connect(userInitiated: true) }

    private func connect(userInitiated: Bool) {
        guard daemonReady else {
            state = .error("Установите Method в /Applications и активируйте демон (Настройки → Демон).")
            daemon.refresh()
            return
        }
        guard !storedProfiles.isEmpty else {
            state = .error("Добавьте конфигурацию или подписку.")
            return
        }

        let epoch = beginNewEpoch()
        if userInitiated {
            // Человек нажал сам — бюджет автовосстановления считаем заново.
            recoveryAttempts.removeAll()
            coreRestartsForCurrentServer = 0
            failoverCooldown.removeAll()
        }
        state = .connecting
        connectAttemptStatus = nil
        adoptedWithoutPlan = false
        // Конфиг собирается заново прямо сейчас, то есть с нынешними
        // правилами: расхождения между списком и поведением больше нет.
        policyPendingRestart = false
        connectTask = Task { [weak self] in
            guard let self else { return }
            // Обновление приложения ломает регистрацию демона: подпись ad-hoc
            // меняется при каждой пересборке, и launchd перестаёт его
            // запускать. Чиним молча — человек не должен знать слово cdhash,
            // чтобы пользоваться VPN.
            if await self.daemon.repairIfStale() {
                self.connectAttemptStatus = "Регистрация демона обновлена, продолжаем…"
            }
            // Демон может быть не просто «не отвечает», а ЗАПРЕЩЁН системой:
            // после обновления приложения macOS переводит элемент входа в
            // «disallowed» и ждёт, пока человек включит его руками. Раньше в
            // этом случае подключение молча не удавалось — на экране ничего,
            // причина только в журнале. Молчаливый отказ хуже честной ошибки:
            // человек жмёт кнопку снова и снова, а делать надо совсем другое.
            let daemonReady = await self.daemon.isEnabled()
            if !daemonReady {
                let why = await self.daemon.diagnoseAuthorization()
                self.connectAttemptStatus = nil
                self.state = .error(why)
                return
            }
            // Режим полос требует не меньше двух НЕЗАВИСИМЫХ способов пройти.
            // На одной оси он бессмысленен: переключаться там не на что, а
            // лишний контур — лишний источник дребезга.
            if await self.laneModeAvailable() {
                await self.connectWithLanes(epoch: epoch)
            } else {
                await self.connectWithFallback(epoch: epoch)
            }
        }
    }

    /// Переподключиться, чтобы новые правила попали в конфиг ядра.
    ///
    /// Отдельно от `connect()`, потому что это действие человека с понятным
    /// намерением: «примени то, что я написал». Разрыв здесь ожидаемый и
    /// объявленный, в отличие от переподключения по аварии.
    func reconnectForPolicy() {
        guard state != .disconnected else { policyPendingRestart = false; return }
        disconnect()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [weak self] in
            self?.connect()
        }
    }

    func disconnect() {
        let epoch = beginNewEpoch()
        connectAttemptStatus = nil
        isFailingOver = false
        adoptedWithoutPlan = false
        orchestrator.stop()
        Task { _ = await daemon.setKillSwitch(enabled: false, interface: "", allowedHosts: []) }
        reconnectManager.stop()
        connectivityMonitor.stop()
        stopCoreWatchdog()
        stopSessionTimer()
        stopTrafficPolling()
        exitIP = nil
        // Показываем намерение сразу: раньше состояние менялось только в
        // ответе демона, и если демон не отвечал (не установлен, отвергает
        // клиента, завис), кнопка навсегда оставалась в положении
        // «Отключить» — отключиться было нечем.
        state = .disconnected
        Task { [weak self] in
            guard let self else { return }
            let stopped = await self.stopTunnelAwait()
            guard epoch == self.connectionEpoch else { return }
            if !stopped {
                // Молчать здесь нельзя: туннель может быть ещё жив, и «не
                // подключено» на экране было бы неправдой.
                self.state = .error(
                    "Демон не подтвердил остановку туннеля — соединение может быть ещё активно. "
                    + "Настройки → Демон → «Перезапустить»."
                )
            }
        }
    }

    /// Новое поколение подключения: отменяет всё, что было начато раньше.
    @discardableResult
    private func beginNewEpoch() -> Int {
        connectionEpoch &+= 1
        connectTask?.cancel(); connectTask = nil
        recoveryTask?.cancel(); recoveryTask = nil
        return connectionEpoch
    }

    /// Перебирает сервер за сервером (начиная с выбранного, затем — по пингу),
    /// пока один реально не заработает (не только процесс жив, а трафик проходит).
    /// Так закрывается ситуация, когда DPI глушит конкретный протокол/порт, а
    /// другой вариант из той же подписки — работает.
    // MARK: - Сценарии

    /// Добавляет правило. Возвращает текст ошибки, если политику с ним
    /// собрать нельзя, — и НЕ сохраняет её: правило, которое не соберётся,
    /// не должно дожить до следующего подключения и сломать его.
    /// Правила изменены, а ядро работает по прежним.
    ///
    /// Маршрутные правила живут в конфиге ядра, и живьём их поменять нечем:
    /// у sing-box через API меняется только выбор внутри группы. Значит новое
    /// правило вступит в силу при следующем подключении — и человек обязан
    /// это видеть. Молча сохранить правило и не применить его хуже, чем
    /// отказаться его принять: список говорит «сделано», а поведение прежнее.
    @Published private(set) var policyPendingRestart = false

    func addRule(domains: [String], lane: String, justification: String) -> String? {
        var next = policy
        // Объяснение относится к полосе, а не к правилу: полоса выпускает
        // трафик открытым, значит она и обязана нести причину.
        if let i = next.lanes.firstIndex(where: { $0.id == lane }), next.lanes[i].allowsDirect {
            let text = justification.trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { next.lanes[i].justification = text }
        }
        next.flows.append(LanePolicy.Flow(match: .domainSuffix(domains), lane: lane))

        // Правило, до которого очередь не дойдёт, принимать нельзя. Ядро
        // берёт первое совпавшее сверху вниз, и такое правило не сработает
        // никогда — а в списке будет выглядеть действующим. Именно так и
        // вышло на живой проверке: старое «2ip.io → весь трафик» перекрыло
        // новое «2ip.io → мимо туннеля», человек сделал всё правильно и не
        // получил ничего. Отказ с указанием виновника честнее.
        if let shadow = next.shadowedFlows().first(where: { $0.index == next.flows.count - 1 }) {
            let earlier = next.flows[shadow.by]
            let laneName = next.lanes.first { $0.id == earlier.lane }?.title ?? earlier.lane
            return "Это правило не сработает: выше уже стоит правило на те же сайты "
                + "в полосе «\(laneName)». Удалите его — иначе ядро возьмёт первое."
        }

        let axes = Set(storedProfiles.map(\.profile).filter(Self.isUsable)
            .map(LaneConfigBuilder.EvasionAxisKey.of))
        do {
            try next.validate(availableAxes: axes)
        } catch {
            return error.localizedDescription
        }
        policy = next
        PolicyStore.save(next)
        if state != .disconnected { policyPendingRestart = true }
        return nil
    }

    func removeRule(_ flow: LanePolicy.Flow) {
        var next = policy
        next.flows.removeAll { $0 == flow }
        policy = next
        if state != .disconnected { policyPendingRestart = true }
        PolicyStore.save(next)
    }

    func resetPolicy() {
        PolicyStore.reset()
        policy = .factory()
    }

    /// Ставит защиту перед подъёмом туннеля.
    ///
    /// Именно ДО: если поднять туннель первым, между его стартом и появлением
    /// правил остаётся окно, в котором трафик уходит открытым. Список адресов
    /// узлов передаётся целиком — движок вправе переключиться на любой из них.
    private func applyKillSwitch(hosts: [String]) async -> String? {
        guard killSwitchEnabled else {
            _ = await daemon.setKillSwitch(enabled: false, interface: "", allowedHosts: [])
            return nil
        }
        // Пакетный фильтр принимает ТОЛЬКО литералы адресов. Доменное имя,
        // отданное ему как есть, правило не создаёт — а у Trojan адрес именно
        // доменный. Разрешаем заранее и добавляем сам домен в список только
        // после того, как узнали его адреса.
        var literals = Set<String>()
        for host in hosts {
            if Self.isIPLiteral(host) { literals.insert(host); continue }
            for address in Self.resolveLiterals(host) { literals.insert(address) }
        }
        // Резолвер, которым ядро узнаёт адрес узла, обязан быть доступен МИМО
        // туннеля: без него ядро не поднимет соединение к ноде с доменным
        // адресом и не стартует вовсе. Без этого правила защита блокирует не
        // утечку, а собственный клиент — что и произошло при первой попытке.
        literals.insert(Self.bootstrapResolver)

        guard !literals.isEmpty else {
            return "Не удалось определить адреса узлов для защиты от утечек"
        }
        let iface = literals.compactMap { NetworkRouteInspector.route(to: $0)?.interface }
            .first { !$0.hasPrefix("utun") }
        guard let iface, !iface.isEmpty else {
            return "Не удалось определить сетевой интерфейс для защиты от утечек"
        }
        let r = await daemon.setKillSwitch(enabled: true, interface: iface,
                                           allowedHosts: literals.sorted())
        return r.ok ? nil : (r.error ?? "Не удалось включить защиту от утечек")
    }

    /// Резолвер из конфига ядра. Держится здесь же, чтобы не разъехаться со
    /// сборщиком: разъезд означал бы заблокированный собственный клиент.
    private static let bootstrapResolver = "77.88.8.8"

    private static func isIPLiteral(_ value: String) -> Bool {
        var v4 = in_addr()
        if value.withCString({ inet_pton(AF_INET, $0, &v4) }) == 1 { return true }
        var v6 = in6_addr()
        return value.withCString { inet_pton(AF_INET6, $0, &v6) } == 1
    }

    /// Адреса домена. Резолвим ДО включения защиты — после включения запрос
    /// уже не пройдёт.
    private static func resolveLiterals(_ host: String) -> [String] {
        var hints = addrinfo(ai_flags: 0, ai_family: AF_UNSPEC, ai_socktype: SOCK_STREAM,
                             ai_protocol: 0, ai_addrlen: 0, ai_canonname: nil,
                             ai_addr: nil, ai_next: nil)
        var result: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &result) == 0, let head = result else { return [] }
        defer { freeaddrinfo(head) }
        var out: [String] = []
        var node: UnsafeMutablePointer<addrinfo>? = head
        while let current = node {
            var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(current.pointee.ai_addr, current.pointee.ai_addrlen,
                           &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST) == 0 {
                let text = String(cString: buffer)
                if !text.isEmpty { out.append(text) }
            }
            node = current.pointee.ai_next
        }
        return out
    }

    /// Хватает ли материала для режима полос.
    private func laneModeAvailable() async -> Bool {
        let usable = storedProfiles.map(\.profile).filter(Self.isUsable)
        let axes = Set(usable.map(LaneConfigBuilder.EvasionAxisKey.of))
        return usable.count >= 2 && axes.count >= 2
    }

    /// Подключение одним туннелем со всеми маршрутами сразу.
    ///
    /// Отличие от перебора кандидатов принципиальное: там на каждый сервер
    /// поднимался свой туннель, и смена сервера означала обрыв всего. Здесь
    /// туннель один, внутри него объявлены все маршруты, сгруппированные по
    /// осям обхода, и смена маршрута — запрос к живому ядру.
    private func connectWithLanes(epoch: Int) async {
        let usable = storedProfiles.map(\.profile).filter(Self.isUsable)
        let policy = self.policy

        let plan: LaneConfigBuilder.Plan
        do {
            plan = try LaneConfigBuilder.build(profiles: usable, policy: policy,
                                               options: Self.singBoxOptions)
        } catch {
            // Политику собрать нельзя — это не повод молча уйти в старый
            // режим: человек должен знать, что его правила не применились.
            state = .error("Не удалось собрать политику: \(error.localizedDescription)")
            return
        }

        // Сменился маршрут — сменился и выходной адрес. Без перезамера в
        // поле «IP-адрес» остаётся адрес прежнего узла, и клиент показывает
        // одно, а сайты видят другое.
        orchestrator.onRouteChanged = { [weak self] in self?.refreshExitIP() }
        connectAttemptStatus = "Поднимаем туннель: маршрутов \(plan.routes.count),"
            + " способов обхода \(plan.axes.count)…"

        guard let json = try? JSONSerialization.data(withJSONObject: plan.config, options: []),
              let text = String(data: json, encoding: .utf8) else {
            state = .error("Не удалось собрать конфигурацию")
            return
        }

        if let ksError = await applyKillSwitch(hosts: plan.hosts) {
            state = .error(ksError)
            return
        }
        let started = await startTunnelAwait(config: text)
        if epoch != connectionEpoch { return }
        if Task.isCancelled { await stopTunnelAwait(); return }
        guard started.ok else {
            // Режим полос не поднялся. Откатываемся на перебор кандидатов:
            // он умеет обходить нерабочие серверы по одному, и лучше рабочий
            // туннель без оркестра, чем никакого.
            await connectWithFallback(epoch: epoch)
            return
        }

        let healthy = await quickConnectivityProbe()
        if epoch != connectionEpoch { return }
        if Task.isCancelled { await stopTunnelAwait(); return }
        guard healthy else {
            await stopTunnelAwait()
            await connectWithFallback(epoch: epoch)
            return
        }

        connectAttemptStatus = nil
        coreRestartsForCurrentServer = 0
        state = .connected
        startSessionTimer()
        startTrafficPolling()
        refreshExitIP()
        connectivityMonitor.start()
        startCoreWatchdog()
        if autoReconnect { reconnectManager.start() }

        orchestrator.start(
            plan: plan,
            endpoint: .init(controller: Self.singBoxOptions.clashController,
                            secret: Self.singBoxOptions.clashSecret)
        )
    }

    private func connectWithFallback(epoch: Int) async {
        let candidates = candidateOrder()
        guard !candidates.isEmpty else {
            state = .error("Нет серверов для подключения")
            return
        }

        // Диагноз локальной причины: если он появился, спорить с сетью не о чем.
        var daemonFailure: String? = nil
        var configFailures = 0

        for (index, profile) in candidates.enumerated() {
            if Task.isCancelled || epoch != connectionEpoch { return }
            // Показываем шаг всегда, а не только при переборе: ожидание ответа
            // демона длится до тридцати секунд, и пустой экран в это время
            // неотличим от зависшего приложения.
            connectAttemptStatus = candidates.count > 1
                ? "Пробуем \(index + 1) из \(candidates.count): \(profile.name)…"
                : "Подключаемся: \(profile.name)…"

            guard let config = try? SingBoxConfigBuilder.buildJSONString(for: profile, options: Self.singBoxOptions) else {
                // Считаем: если конфиг не собрался НИ РАЗУ, дело в данных
                // подписки, а не в сети, и сообщать надо именно это.
                configFailures += 1
                continue
            }

            if let ksError = await applyKillSwitch(hosts: [profile.host]) {
                daemonFailure = ksError
                break
            }
            let started = await startTunnelAwait(config: config)
            // Прерывание: останавливаем туннель ТОЛЬКО если поколение наше.
            // Если поколение сменилось, значит уже идёт новый connect (он
            // поднимет свой туннель) или disconnect (он сам его остановит), и
            // наш запоздалый stop убил бы чужой, только что поднятый туннель.
            if epoch != connectionEpoch { return }
            if Task.isCancelled { await stopTunnelAwait(); return }
            guard started.ok else {
                if let err = started.err, !err.isEmpty { daemonFailure = err }
                // Демон молчит — дальше перебирать нечего, причина не в сервере.
                if started.daemonUnavailable { break }
                continue
            }

            let healthy = await quickConnectivityProbe()
            // Прерывание: останавливаем туннель ТОЛЬКО если поколение наше.
            // Если поколение сменилось, значит уже идёт новый connect (он
            // поднимет свой туннель) или disconnect (он сам его остановит), и
            // наш запоздалый stop убил бы чужой, только что поднятый туннель.
            if epoch != connectionEpoch { return }
            if Task.isCancelled { await stopTunnelAwait(); return }

            if healthy {
                selectedID = profile.id
                connectAttemptStatus = nil
                coreRestartsForCurrentServer = 0
                state = .connected
                startSessionTimer()
                startTrafficPolling()
                refreshExitIP()
                connectivityMonitor.start()
                startCoreWatchdog()
                if autoReconnect { reconnectManager.start() }
                return
            }
            await stopTunnelAwait()
        }

        if Task.isCancelled || epoch != connectionEpoch { return }
        connectAttemptStatus = nil
        // Если туннель не поднялся ни разу из-за демона, дело не в сети:
        // все кандидаты падают одинаково и одинаково быстро. Гипотеза про
        // блокировку уместна только тогда, когда ядро реально стартовало.
        if let local = daemonFailure {
            state = .error(local)
            return
        }
        if configFailures == candidates.count {
            state = .error("Не удалось собрать конфигурацию ни для одного сервера — данные подписки повреждены.")
            return
        }
        state = .error(candidates.count > 1
            ? "Ни один из \(candidates.count) серверов не ответил — сеть блокирует протокол."
            : "Не удалось подключиться.")
    }

    /// Порядок перебора: сначала выбранный сервер, затем остальные по возрастанию пинга.
    /// Серверы в карантине (только что подвели) уходят в конец, а не выбрасываются:
    /// пробовать заведомо мёртвый вариант поздно, но лучше, чем не пробовать ничего.
    private func candidateOrder() -> [ServerProfile] {
        let now = Date()
        func quarantined(_ id: UUID) -> Bool { (failoverCooldown[id] ?? .distantPast) > now }

        let usable = storedProfiles.map(\.profile).filter(Self.isUsable)
        var seen = Set<UUID>()
        var fresh: [ServerProfile] = []
        if let first = selected, Self.isUsable(first), !quarantined(first.id) {
            fresh.append(first); seen.insert(first.id)
        }
        fresh.append(contentsOf: usable
            .filter { !seen.contains($0.id) && !quarantined($0.id) }
            .sorted { (pings[$0.id] ?? Int.max) < (pings[$1.id] ?? Int.max) })

        let freshIDs = Set(fresh.map(\.id))
        let quarantinedRest = usable
            .filter { !freshIDs.contains($0.id) }
            .sorted { (pings[$0.id] ?? Int.max) < (pings[$1.id] ?? Int.max) }

        return Array((fresh + quarantinedRest).prefix(Self.maxFallbackCandidates))
    }

    /// `daemonUnavailable` — демон не ответил вовсе (не установлен, отверг
    /// клиента, завис). Это беда машины, а не сервера, и перебирать остальных
    /// кандидатов бессмысленно: каждый упрётся в то же самое и потратит на это
    /// свои тридцать секунд.
    private func startTunnelAwait(config: String) async -> (ok: Bool, err: String?, daemonUnavailable: Bool) {
        await withCheckedContinuation { cont in
            let once = OnceFlag()
            daemon.withServiceAsync(timeout: Self.daemonTimeout, onUnavailable: { [weak self] in
                // Настоящая причина, а не «Демон недоступен»: отказ демона
                // принять клиента внешне неотличим от блокировки сети, и
                // человек уходит искать обход при исправных нодах.
                guard once.fire() else { return }
                cont.resume(returning: (false, self?.daemon.statusMessage ?? "Демон недоступен", true))
            }) { service, done in
                service.startTunnel(config: config) { ok, err in
                    done()
                    // Ответ И обрыв могут прийти оба: XPC не обещает, что
                    // обработчик прерывания не сработает после reply. Второе
                    // возобновление континуации — мгновенное падение процесса.
                    guard once.fire() else { return }
                    cont.resume(returning: (ok, err, false))
                }
            }
        }
    }

    /// `true` — демон подтвердил остановку.
    @discardableResult
    private func stopTunnelAwait() async -> Bool {
        await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            let once = OnceFlag()
            daemon.withServiceAsync(timeout: Self.daemonTimeout, onUnavailable: {
                if once.fire() { cont.resume(returning: false) }
            }) { service, done in
                service.stopTunnel { ok, _ in
                    done()
                    if once.fire() { cont.resume(returning: ok) }
                }
            }
        }
    }

    private func reconnectTunnelAwait() async -> Bool {
        await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            let once = OnceFlag()
            daemon.withServiceAsync(timeout: Self.daemonTimeout, onUnavailable: {
                if once.fire() { cont.resume(returning: false) }
            }) { service, done in
                service.reconnectTunnel { ok, _ in
                    done()
                    if once.fire() { cont.resume(returning: ok) }
                }
            }
        }
    }

    /// `nil` — демон не ответил, то есть ответа «жив» или «мёртв» у нас нет.
    /// Разница принципиальная: недоступный демон — не повод объявлять ядро
    /// мёртвым и рвать рабочий туннель.
    private func isTunnelRunningAwait() async -> Bool? {
        await withCheckedContinuation { (cont: CheckedContinuation<Bool?, Never>) in
            let once = OnceFlag()
            daemon.withServiceAsync(timeout: Self.daemonQuickTimeout, onUnavailable: {
                if once.fire() { cont.resume(returning: nil) }
            }) { service, done in
                service.isTunnelRunning { running in
                    done()
                    if once.fire() { cont.resume(returning: running) }
                }
            }
        }
    }

    /// Реальная проверка сквозной связности (не только «процесс жив») — короткий
    /// HTTPS-запрос через уже поднятый туннель.
    /// - Parameter thorough: перебрать все проверочные адреса. На переборе
    ///   кандидатов — нет (иначе секунды множатся на число серверов), при
    ///   восстановлении — да (там ошибиться дороже).
    private func quickConnectivityProbe(thorough: Bool = false) async -> Bool {
        await ConnectivityMonitor.probeOnce(tryAll: thorough)
    }

    func selectProfile(_ id: UUID) {
        selectedID = id
        guard isConnected else { return }
        disconnect()
        // Пауза нужна, чтобы демон успел снять utun; поколение проверяем,
        // потому что за эти полсекунды человек мог передумать и нажать что
        // угодно ещё.
        let epoch = connectionEpoch
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: 500_000_000)
            guard let self, epoch == self.connectionEpoch else { return }
            self.connect()
        }
    }

    private func handleAutoReconnect() {
        guard autoReconnect, state == .connected, recoveryTask == nil, !reconnectInFlight else { return }
        reconnectInFlight = true
        let epoch = connectionEpoch
        Task { [weak self] in
            guard let self else { return }
            let ok = await self.reconnectTunnelAwait()
            self.reconnectInFlight = false
            guard epoch == self.connectionEpoch, self.state == .connected else { return }
            // Раньше результат не проверялся вовсе: после пробуждения ноутбука
            // ядро могло не подняться, а на экране продолжало гореть «Защищено».
            if !ok { self.beginRecovery(reason: "Туннель не восстановился после пробуждения") }
        }
    }

    // MARK: - Восстановление: зависшее ядро, потеря связности, DPI

    /// Связность не подтверждается — трафик не идёт, хотя формально «подключено».
    private func handleUnhealthyConnection() {
        beginRecovery(reason: "Трафик через туннель не проходит")
    }

    private func beginRecovery(reason: String) {
        guard state == .connected, recoveryTask == nil else { return }
        let epoch = connectionEpoch
        recoveryGeneration &+= 1
        let token = recoveryGeneration
        recoveryTask = Task { [weak self] in
            await self?.recover(reason: reason, epoch: epoch, token: token)
        }
    }

    /// Лестница восстановления: сперва перезапуск ядра тем же сервером, затем
    /// смена сервера, и в конце — честная ошибка с реальным отключением.
    ///
    /// Ни один шаг не выполняется молча: пока связность не подтверждена,
    /// состояние — «Подключение…», а не «Защищено». Правило проекта: молчаливый
    /// отказ, выданный за успех, опаснее честной ошибки.
    private func recover(reason: String, epoch: Int, token: Int) async {
        // Снимаем ссылку только на СВОЁ восстановление: к моменту выхода отсюда
        // уже могло начаться следующее, и обнулить его ссылку значило бы
        // разрешить третье параллельно.
        defer { if token == recoveryGeneration { recoveryTask = nil } }
        guard epoch == connectionEpoch else { return }

        connectivityMonitor.stop()
        stopCoreWatchdog()

        guard allowRecoveryAttempt() else {
            await giveUp(
                reason: "\(reason). Восстановить связь не удалось \(Self.maxRecoveriesInWindow) раз подряд — "
                    + "туннель отключён. Проверьте сеть или выберите другой сервер.",
                epoch: epoch
            )
            return
        }

        // Шаг 1. Перезапуск ядра с тем же сервером.
        if coreRestartsForCurrentServer < Self.maxCoreRestartsPerServer {
            coreRestartsForCurrentServer += 1
            state = .connecting
            connectAttemptStatus = "\(reason) — перезапускаем ядро "
                + "(\(coreRestartsForCurrentServer) из \(Self.maxCoreRestartsPerServer))…"

            let restarted = await reconnectTunnelAwait()
            guard !Task.isCancelled, epoch == connectionEpoch else { return }

            if restarted, await quickConnectivityProbe(thorough: true) {
                guard !Task.isCancelled, epoch == connectionEpoch else { return }
                connectAttemptStatus = nil
                state = .connected
                connectivityMonitor.start()
                startCoreWatchdog()
                refreshExitIP()
                return
            }
            guard !Task.isCancelled, epoch == connectionEpoch else { return }
        }

        // Шаг 2. Ядро перезапускали — не помогло. Меняем сервер.
        guard let current = selected, let next = nextFailoverCandidate(excluding: current.id) else {
            await giveUp(reason: "\(reason), а других рабочих серверов нет — туннель отключён.", epoch: epoch)
            return
        }
        failoverCooldown[current.id] = Date().addingTimeInterval(Self.failoverCooldownSeconds)
        isFailingOver = true
        selectedID = next.id
        coreRestartsForCurrentServer = 0
        await stopTunnelAwait()
        guard !Task.isCancelled, epoch == connectionEpoch else { isFailingOver = false; return }
        isFailingOver = false
        connect(userInitiated: false)
    }

    /// Отключение с честной причиной на экране.
    private func giveUp(reason: String, epoch: Int) async {
        connectAttemptStatus = nil
        isFailingOver = false
        reconnectManager.stop()
        connectivityMonitor.stop()
        stopCoreWatchdog()
        stopSessionTimer()
        stopTrafficPolling()
        exitIP = nil
        await stopTunnelAwait()
        guard epoch == connectionEpoch else { return }
        state = .error(reason)
    }

    /// Лимит попыток за окно времени. Без него перезапуски крутились бы вечно:
    /// связность не проверяется — перезапуск — снова не проверяется, и так до
    /// разряда батареи, при неизменном «всё хорошо» на экране.
    private func allowRecoveryAttempt() -> Bool {
        let now = Date()
        recoveryAttempts.removeAll { now.timeIntervalSince($0) > Self.recoveryWindow }
        guard recoveryAttempts.count < Self.maxRecoveriesInWindow else { return false }
        recoveryAttempts.append(now)
        return true
    }

    private func nextFailoverCandidate(excluding id: UUID) -> ServerProfile? {
        let now = Date()
        let candidates = storedProfiles.map(\.profile).filter {
            $0.id != id && Self.isUsable($0) && (failoverCooldown[$0.id].map { $0 < now } ?? true)
        }
        return candidates
            .sorted { (pings[$0.id] ?? Int.max) < (pings[$1.id] ?? Int.max) }
            .first
    }

    // MARK: - Сторож ядра

    /// Ядро может умереть само (упало, демон исчерпал лимит автоперезапусков),
    /// и до этого сторожа единственным признаком была потеря связности — то
    /// есть 24 секунды с надписью «Защищено» на экране при мёртвом туннеле.
    private func startCoreWatchdog() {
        coreWatchdogTimer?.invalidate()
        let t = Timer(timeInterval: Self.coreWatchdogInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.checkCoreAlive() }
        }
        RunLoop.main.add(t, forMode: .common)
        coreWatchdogTimer = t
    }

    private func stopCoreWatchdog() {
        coreWatchdogTimer?.invalidate()
        coreWatchdogTimer = nil
    }

    private func checkCoreAlive() async {
        guard state == .connected, recoveryTask == nil else { return }
        let epoch = connectionEpoch
        guard let alive = await isTunnelRunningAwait() else { return }
        guard epoch == connectionEpoch, state == .connected else { return }
        if !alive { beginRecovery(reason: "Ядро sing-box остановилось") }
    }

    // MARK: - Пинги / внешний IP

    func measurePings() {
        // `UInt16(port)` — ровно тот вызов, который ронял приложение: порт
        // приходит из подписки, `999999` там совершенно законен с точки зрения
        // URLComponents, а перевод в UInt16 не бросает ошибку, а убивает
        // процесс. И убивал бы при КАЖДОМ запуске, потому что пинги считаются
        // в bootstrap(), а профиль лежит в хранилище.
        let usable: [(UUID, String, UInt16)] = storedProfiles.compactMap { sp in
            guard Self.isUsable(sp.profile), let port = UInt16(exactly: sp.profile.port) else { return nil }
            return (sp.profile.id, sp.profile.host, port)
        }
        // Каждый замер — отдельный процесс /sbin/ping. Подписка на тысячу
        // серверов означала бы тысячу процессов разом; предел обязателен.
        let targets = Array(usable.prefix(Self.maxPingTargets))

        guard !targets.isEmpty, !isMeasuringPings else { return }
        isMeasuringPings = true
        Task { [weak self] in
            let limit = Self.maxPingConcurrency
            let results = await withTaskGroup(of: (UUID, Int?).self) { group in
                var out: [(UUID, Int?)] = []
                var launched = 0
                for (id, host, port) in targets {
                    if launched >= limit {
                        if let r = await group.next() { out.append(r) }
                    }
                    group.addTask { (id, await PingService.measure(host: host, port: port)) }
                    launched += 1
                }
                for await r in group { out.append(r) }
                return out
            }
            await MainActor.run {
                guard let self else { return }
                for (id, ms) in results {
                    if let ms { self.pings[id] = ms } else { self.pings.removeValue(forKey: id) }
                }
                self.isMeasuringPings = false
            }
        }
    }

    func ping(for id: UUID?) -> Int? { id.flatMap { pings[$0] } }

    func refreshExitIP() {
        let epoch = connectionEpoch
        Task { [weak self] in
            guard let url = URL(string: "https://api.ipify.org") else { return }
            var req = URLRequest(url: url, timeoutInterval: 6)
            req.cachePolicy = .reloadIgnoringLocalCacheData
            if let (data, resp) = try? await URLSession.shared.data(for: req),
               (resp as? HTTPURLResponse)?.statusCode == 200,
               let raw = String(data: data, encoding: .utf8) {
                // Ответ приходит с чужого сервера: показываем только то, что
                // похоже на адрес, и не длиннее адреса. Иначе в интерфейс
                // попадёт страница-заглушка провайдера целиком.
                let ip = raw.trimmingCharacters(in: .whitespacesAndNewlines)
                guard ip.count <= 45, !ip.isEmpty,
                      ip.allSatisfy({ $0.isHexDigit || $0 == "." || $0 == ":" }) else { return }
                await MainActor.run {
                    guard let self, epoch == self.connectionEpoch else { return }
                    // Запоздалый ответ не имеет права рисовать адрес выхода
                    // поверх уже отключённого туннеля.
                    guard self.state == .connected else { return }
                    self.exitIP = ip
                }
            }
        }
    }

    // MARK: - Таймер / трафик

    private func startSessionTimer() {
        sessionStartedAt = Date()
        sessionSeconds = 0
        sessionTimer?.invalidate()
        // .common: в режиме по умолчанию таймер замирает, пока открыто меню в
        // строке статуса, и время сессии начинает отставать.
        let t = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let start = self?.sessionStartedAt else { return }
                self?.sessionSeconds = Int(Date().timeIntervalSince(start))
            }
        }
        RunLoop.main.add(t, forMode: .common)
        sessionTimer = t
    }

    private func stopSessionTimer() {
        sessionTimer?.invalidate(); sessionTimer = nil
        sessionStartedAt = nil; sessionSeconds = 0
    }

    private func startTrafficPolling() {
        lastTraffic = nil
        downloadSpeed = 0; uploadSpeed = 0
        downloadHistory = Array(repeating: 0, count: Self.historyLength)
        trafficTimer?.invalidate()
        let t = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in await self?.pollTraffic() }
        }
        RunLoop.main.add(t, forMode: .common)
        trafficTimer = t
    }

    private func stopTrafficPolling() {
        trafficTimer?.invalidate(); trafficTimer = nil
        lastTraffic = nil
        downloadSpeed = 0; uploadSpeed = 0
        downloadHistory = []
    }

    private func pollTraffic() async {
        guard let snap = await ClashAPIClient.fetchTraffic(
            controller: Self.singBoxOptions.clashController,
            secret: Self.singBoxOptions.clashSecret
        ) else { return }
        defer { lastTraffic = snap }
        guard let prev = lastTraffic else { return }
        let dDown = max(0, snap.downloadTotal - prev.downloadTotal)
        let dUp = max(0, snap.uploadTotal - prev.uploadTotal)
        downloadSpeed = Double(dDown)
        uploadSpeed = Double(dUp)
        var h = downloadHistory
        h.append(Double(dDown))
        if h.count > Self.historyLength { h.removeFirst(h.count - Self.historyLength) }
        downloadHistory = h
    }

    static func formatSpeed(_ bytesPerSec: Double) -> String { formatBytes(bytesPerSec) + "/s" }

    static func formatBytes(_ bytes: Double) -> String {
        let units = ["B", "KB", "MB", "GB", "TB"]
        var value = bytes, i = 0
        while value >= 1024 && i < units.count - 1 { value /= 1024; i += 1 }
        return i == 0 ? String(format: "%.0f %@", value, units[i])
                      : String(format: "%.1f %@", value, units[i])
    }

    func formattedDuration() -> String {
        let h = sessionSeconds / 3600, m = (sessionSeconds % 3600) / 60, s = sessionSeconds % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%02d:%02d", m, s)
    }
}
