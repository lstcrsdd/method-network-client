import AppKit
import Foundation
import Security
import ServiceManagement

/// Привилегированный демон для standalone-приложения **Method**.
/// Своё пространство имён (`network.method.client.helper`), чтобы не пересекаться
/// с Method VPN — оба приложения могут стоять рядом.
/// Публикуемые свойства обязаны меняться с главного потока: SwiftUI на
/// изменение из фонового отвечает «Publishing changes from background thread»
/// и повреждением состояния интерфейса. Пометка на весь класс дешевле, чем
/// вспоминать про это в каждом методе.
@MainActor
final class MethodDaemon: ObservableObject {
    static let shared = MethodDaemon()

    static let machServiceName = "network.method.client.helper"
    private let plistName = "network.method.client.helper.plist"
    private let applicationsPath = "/Applications/Method.app"

    @Published var status: SMAppService.Status = .notRegistered
    @Published var statusMessage = ""
    @Published var isBusy = false
    @Published var needsApplicationsInstall = false

    var isRunningFromApplications: Bool {
        Bundle.main.bundleURL.path == applicationsPath
    }

    init() { refresh() }

    /// Перерегистрирует демона, если система считает его установленным, но
    /// запустить не может.
    ///
    /// Так выглядит обновление приложения. Приложение подписано ad-hoc, и при
    /// каждой пересборке у него новый cdhash; запись, сделанная для прежнего
    /// бандла, перестаёт совпадать, и launchd отказывается запускать демона
    /// с «Could not find and/or execute program», exit(78). Приложение при
    /// этом честно ждёт ответа, которого не будет, и выглядит зависшим.
    ///
    /// Снятие и повторная регистрация решают это без участия человека. Делаем
    /// только когда демон ЧИСЛИТСЯ установленным, но не отвечает: слепая
    /// перерегистрация при каждом запуске просила бы пароль на ровном месте.
    func repairIfStale() async -> Bool {
        refresh()
        guard status == .enabled else { return false }
        if await ping() { return false }

        statusMessage = "Обновляем регистрацию демона…"
        let service = SMAppService.daemon(plistName: plistName)
        // Асинхронный вариант: у синхронного в новых SDK есть одноимённый
        // async-двойник, и без await компилятор выбирает не тот.
        try? await service.unregister()
        // Небольшая пауза: снятие регистрации асинхронно внутри launchd, и
        // немедленная повторная регистрация подхватывает старую запись.
        try? await Task.sleep(for: .milliseconds(600))
        do {
            try service.register()
            statusMessage = "Регистрация демона обновлена."
        } catch {
            statusMessage = "Не удалось обновить регистрацию: \(error.localizedDescription)"
            refresh()
            return false
        }
        refresh()
        return await ping()
    }

    /// Отвечает ли демон вообще. Короткий срок: цель — отличить «не отвечает»
    /// от «отвечает медленно», а не дождаться ответа любой ценой.
    private func ping() async -> Bool {
        await withCheckedContinuation { cont in
            let once = OnceFlag()
            withServiceAsync(onUnavailable: {
                if once.fire() { cont.resume(returning: false) }
            }) { service, done in
                service.getVersion { _ in
                    if once.fire() { cont.resume(returning: true) }
                    done()
                }
            }
        }
    }

    func refresh() {
        // @Published читает SwiftUI, и запись не с главного потока роняет
        // приложение жёстко (без исключения, прямо в отрисовке). Вызвать
        // refresh() из фонового обработчика легко — страхуемся здесь, а не
        // надеемся на дисциплину всех вызывающих.
        guard Thread.isMainThread else {
            DispatchQueue.main.async { [weak self] in self?.refresh() }
            return
        }
        needsApplicationsInstall = !isRunningFromApplications
        status = SMAppService.daemon(plistName: plistName).status
        // Секунда форы: сразу после register() демон уже числится enabled, но
        // launchd мог ещё не поднять процесс — без паузы приложение оклеветало
        // бы только что установленный демон.
        guard !handshakeInFlight else { return }
        handshakeInFlight = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
            self?.handshakeInFlight = false
            self?.verifyHandshake()
        }
    }

    func copyToApplications() {
        isBusy = true
        statusMessage = "Копируем в /Applications…"
        let source = Bundle.main.bundleURL.path.replacingOccurrences(of: "'", with: "'\\''")
        let staging = "\(applicationsPath).new"
        // Атомарно: сначала ditto во временный путь, и только при УСПЕХЕ убираем
        // старое приложение и переименовываем новое на его место. Раньше было
        // rm -rf сразу + ditto вторым шагом — если ditto падал (или скрипт обрывался
        // на полпути), /Applications оставался БЕЗ приложения вообще (что и
        // произошло на практике).
        let script = """
        do shell script "rm -rf '\(staging)' && ditto '\(source)' '\(staging)' && rm -rf '\(applicationsPath)' && mv '\(staging)' '\(applicationsPath)'" with administrator privileges
        """
        DispatchQueue.global(qos: .userInitiated).async {
            var error: NSDictionary?
            NSAppleScript(source: script)?.executeAndReturnError(&error)
            DispatchQueue.main.async {
                self.isBusy = false
                if let error {
                    self.statusMessage = (error[NSAppleScript.errorMessage] as? String) ?? "Ошибка копирования"
                } else {
                    self.statusMessage = "Откройте /Applications/Method.app"
                    NSWorkspace.shared.open(URL(fileURLWithPath: self.applicationsPath))
                }
                self.refresh()
            }
        }
    }

    func install() {
        guard isRunningFromApplications else {
            statusMessage = "Сначала установите приложение в /Applications."
            needsApplicationsInstall = true
            return
        }
        isBusy = true
        do {
            try SMAppService.daemon(plistName: plistName).register()
            statusMessage = "Демон установлен."
        } catch {
            statusMessage = error.localizedDescription
        }
        isBusy = false
        refresh()
    }

    func uninstall() {
        isBusy = true
        do {
            try SMAppService.daemon(plistName: plistName).unregister()
            statusMessage = "Демон удалён."
        } catch {
            statusMessage = error.localizedDescription
        }
        isBusy = false
        refresh()
    }

    func reinstall() {
        uninstall()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { self.install() }
    }

    /// Готов ли демон принимать команды прямо сейчас.
    ///
    /// Отдельным методом, потому что спрашивают из другого потока: статус
    /// живёт на главном, а решение о подключении принимается в задаче.
    func isEnabled() -> Bool {
        refresh()
        return status == .enabled
    }

    func openSystemSettingsLoginItems() {
        SMAppService.openSystemSettingsLoginItems()
    }

    // MARK: - Диагноз вместо «Operation not permitted»

    /// Почему демон мог не ответить — человеческим языком и с указанием, что нажать.
    ///
    /// Демон отвергает чужого клиента ДО обмена сообщениями, поэтому объяснить
    /// причину по XPC он не может физически: соединение просто закрывается.
    /// Поэтому тот же самый диагноз приложение ставит себе само — по тем же
    /// данным, по которым его ставит демон (см. `XPCClientValidator`).
    /// Включить или снять kill switch.
    ///
    /// Правило пишется на конкретный физический интерфейс и пропускает только
    /// адреса узлов: всё остальное мимо туннеля блокируется. Поэтому список
    /// узлов обязан быть ПОЛНЫМ — если маршрут, на который движок захочет
    /// переключиться, в него не попал, переключение упрётся в собственную же
    /// защиту.
    func setKillSwitch(enabled: Bool, interface: String,
                       allowedHosts: [String]) async -> (ok: Bool, error: String?) {
        await withCheckedContinuation { cont in
            let once = OnceFlag()
            withServiceAsync(onUnavailable: {
                if once.fire() { cont.resume(returning: (false, self.diagnoseAuthorization())) }
            }) { service, done in
                service.setKillSwitch(enabled: enabled, interface: interface,
                                      allowedServerHosts: allowedHosts) { ok, err in
                    if once.fire() { cont.resume(returning: (ok, err)) }
                    done()
                }
            }
        }
    }

    func diagnoseAuthorization() -> String {
        if !isRunningFromApplications {
            return "Method запущен не из /Applications (сейчас — \(Bundle.main.bundleURL.path)). "
                + "Демон выполняет команды только от приложения, рядом с которым установлен. "
                + "Нажмите «Копировать», затем откройте /Applications/Method.app."
        }
        switch status {
        case .notRegistered:
            return "Демон не активирован. Настройки → Демон → «Активировать»."
        case .requiresApproval:
            return "macOS ждёт подтверждения демона: "
                + "Настройки системы → Основные → Элементы входа и расширения."
        case .notFound:
            return "Демон не найден внутри Method.app — комплект неполный. "
                + "Пересоберите приложение и скопируйте его в /Applications заново."
        case .enabled:
            return bundleIntegrityComplaint()
                ?? "Демон не ответил. Настройки → Демон → «Перезапустить»."
        @unknown default:
            return "Состояние демона неизвестно. Настройки → Демон → «Перезапустить»."
        }
    }

    /// Жалоба на подпись собственного бандла, если она не сходится.
    ///
    /// Ровно эта же проверка стоит в демоне: он выводит требование к клиенту из
    /// бандла, внутри которого лежит, и отказывается работать, если бандл
    /// изменили после подписи (иначе незамеченной прошла бы подмена
    /// `Contents/Resources/sing-box`, который демон запускает от root).
    /// Типичный сценарий у разработчика: пересобрали в Xcode и скопировали в
    /// /Applications только часть комплекта.
    private var integrityVerdict: (checkedAt: Date, complaint: String?)?
    private static let integrityCacheTTL: TimeInterval = 60

    private func bundleIntegrityComplaint() -> String? {
        // Проверка рекурсивно хеширует весь бандл — а внутри лежит пятидесяти-
        // мегабайтный sing-box. Один раз это незаметно, но `diagnoseAuthorization`
        // вызывается на КАЖДОМ неудачном обращении к демону, а сторож ядра
        // обращается к нему каждые шесть секунд: при неустановленном демоне
        // главный поток уходил бы на эту проверку раз в шесть секунд без конца.
        // Минуты кэша хватает, чтобы диагноз оставался свежим для человека.
        if let cached = integrityVerdict,
           Date().timeIntervalSince(cached.checkedAt) < Self.integrityCacheTTL {
            return cached.complaint
        }
        let complaint = computeBundleIntegrityComplaint()
        integrityVerdict = (Date(), complaint)
        return complaint
    }

    private func computeBundleIntegrityComplaint() -> String? {
        var staticCode: SecStaticCode?
        guard SecStaticCodeCreateWithPath(
            Bundle.main.bundleURL as CFURL,
            SecCSFlags(),
            &staticCode
        ) == errSecSuccess, let staticCode else {
            return "Приложение не подписано. Пересоберите Method и скопируйте его в /Applications."
        }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures
            | kSecCSCheckNestedCode
            | kSecCSStrictValidate)
        guard SecStaticCodeCheckValidity(staticCode, flags, nil) != errSecSuccess else {
            return nil
        }
        return "Файлы Method.app изменились после подписи, и демон перестал доверять приложению. "
            + "Скопируйте приложение в /Applications заново (кнопка «Копировать»), "
            + "затем нажмите «Перезапустить»."
    }

    // MARK: - Рукопожатие по ревизии протокола

    private var handshakeInFlight = false

    /// Проверяет, что установленный демон знает текущий набор XPC-методов.
    ///
    /// Без этого «демон не отвечает» и «демон отвечает, но старый» выглядят
    /// одинаково — зависанием: NSXPC молча отбрасывает сообщение с неизвестным
    /// селектором, и reply-блок не вызывается никогда (CLAUDE.md §7.1).
    private func verifyHandshake() {
        guard status == .enabled else { return }
        withServiceAsync(onUnavailable: nil) { service, done in
            service.getVersion { version in
                done()
                let revision = Self.parseRevision(from: version)
                guard revision != methodVPNHelperProtocolRevision else { return }
                let found = revision.map(String.init) ?? "неизвестна"
                DispatchQueue.main.async {
                    self.statusMessage = "Установлен демон другой ревизии "
                        + "(\(found), нужна \(methodVPNHelperProtocolRevision)). "
                        + "Настройки → Демон → «Перезапустить»."
                }
            }
        }
    }

    /// Вытаскивает N из строки вида "1.0.0 (Method Helper) proto=2".
    static func parseRevision(from version: String) -> Int? {
        guard let range = version.range(of: methodVPNHelperRevisionPrefix) else { return nil }
        return Int(version[range.upperBound...].prefix { $0.isNumber })
    }

    // MARK: - XPC

    /// Гарантия «ровно один исход»: либо вызывающий завершил вызов сам, либо мы
    /// честно сообщили, что демон недоступен.
    private final class OneShot {
        private let lock = NSLock()
        private var fired = false
        func fire() -> Bool {
            lock.lock(); defer { lock.unlock() }
            if fired { return false }
            fired = true
            return true
        }
    }

    /// Выполняет команду на демоне.
    ///
    /// `onUnavailable` вызывается при ЛЮБОМ обрыве связи, включая отказ демона
    /// принять подключение. Это принципиально: демон теперь проверяет клиента и
    /// может закрыть соединение молча, а до этой правки такой отказ означал, что
    /// reply-блок не вызовется никогда — `startTunnelAwait` ждал бы вечно, и
    /// приложение висело бы на «Подключаемся…» без единого слова о причине.
    ///
    /// `timeout` закрывает второй способ зависнуть намертво: демон принял
    /// соединение, но ответа не будет никогда. Так бывает, когда его очередь
    /// заблокирована дочерним процессом (`sing-box check` пишет в переполненный
    /// пайп, `waitUntilExit` не возвращается) или когда установлен демон старой
    /// ревизии — NSXPC молча отбрасывает неизвестный селектор (CLAUDE.md §7.1).
    /// Ни обрыва, ни ошибки при этом нет, поэтому единственный выход — срок.
    func withServiceAsync(
        timeout: TimeInterval = 30,
        onUnavailable: (() -> Void)? = nil,
        _ block: @escaping (MethodVPNHelperProtocol, @escaping () -> Void) -> Void
    ) {
        let connection = NSXPCConnection(machServiceName: Self.machServiceName, options: .privileged)
        connection.remoteObjectInterface = NSXPCInterface(with: MethodVPNHelperProtocol.self)
        let once = OneShot()

        let fail: (String?) -> Void = { [weak self] reason in
            guard once.fire() else { return }
            connection.invalidate()
            DispatchQueue.main.async {
                if let self { self.statusMessage = reason ?? self.diagnoseAuthorization() }
                onUnavailable?()
            }
        }
        connection.interruptionHandler = { fail(nil) }
        connection.invalidationHandler = { fail(nil) }
        connection.resume()

        // Срок на ответ. Сработает вхолостую, если вызов уже завершился:
        // `OneShot` пропускает ровно один исход.
        DispatchQueue.main.asyncAfter(deadline: .now() + timeout) {
            fail("Демон не ответил за \(Int(timeout)) с. Настройки → Демон → «Перезапустить».")
        }

        guard let proxy = connection.remoteObjectProxyWithErrorHandler({ _ in fail(nil) })
            as? MethodVPNHelperProtocol else {
            fail(nil)
            return
        }
        block(proxy) {
            guard once.fire() else { return }
            connection.invalidate()
        }
    }
}
