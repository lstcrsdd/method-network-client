import Foundation
import os.log

class HelperService: NSObject, MethodVPNHelperProtocol {
    private let logger = OSLog(subsystem: "network.method.client.helper", category: "HelperService")

    /// Ответить можно ровно один раз: повторный вызов reply-блока XPC — это
    /// исключение и смерть демона от root, а вместе с ним потеря управления
    /// уже поднятым туннелем. Обёртка стоит на всех методах, чтобы ни одна
    /// будущая правка не смогла ответить дважды.
    private static func once<A, B>(_ reply: @escaping (A, B) -> Void) -> (A, B) -> Void {
        let flag = ReplyOnce()
        return { a, b in if flag.fire() { reply(a, b) } }
    }

    private final class ReplyOnce: @unchecked Sendable {
        private let lock = NSLock()
        private var fired = false
        func fire() -> Bool {
            lock.lock(); defer { lock.unlock() }
            if fired { return false }
            fired = true
            return true
        }
    }

    func getVersion(withReply reply: @escaping (String) -> Void) {
        // Ревизия обязана быть в ответе: по ней приложение отличает «демон не
        // отвечает» от «демон отвечает, но старый». NSXPC молча отбрасывает
        // неизвестный селектор, и без этого маркера оба случая выглядят
        // одинаково — зависанием (CLAUDE.md §7.1).
        reply("1.0.0 (Method Helper) \(methodVPNHelperRevisionPrefix)\(methodVPNHelperProtocolRevision)")
    }

    func startTunnel(config: String, withReply rawReply: @escaping (Bool, String?) -> Void) {
        let reply = Self.once(rawReply)
        guard config.utf8.count <= 1_048_576 else {
            reply(false, "Конфигурация превышает допустимый размер")
            return
        }
        // Глубину проверяем ДО разбора: `JSONSerialization` рекурсивна, а
        // обработчик XPC живёт на потоке очереди с маленьким стеком — глубоко
        // вложенный документ там не возвращает ошибку, а убивает демон.
        // Демон работает от root и держит туннель: его смерть означает, что
        // управлять поднятым туннелем больше нечем (см. `JSONDepthGuard`).
        guard JSONDepthGuard.isSafe(config) else {
            reply(false, "Конфигурация вложена слишком глубоко (предел \(JSONDepthGuard.maxDepth))")
            return
        }
        // Пустая или не-JSON строка до этой проверки уходила в sing-box и
        // возвращалась невнятной диагностикой ядра.
        guard let data = config.data(using: .utf8),
              (try? JSONSerialization.jsonObject(with: data)) != nil else {
            reply(false, "Конфигурация не является корректным JSON")
            return
        }
        guard let singboxURL = resolveSingBoxURL() else {
            reply(false, "sing-box не найден в .app")
            return
        }
        do {
            try SingboxRunner.shared.start(config: config, executableURL: singboxURL)
            reply(true, nil)
        } catch {
            os_log("startTunnel: %{public}@", log: logger, type: .error, error.localizedDescription)
            reply(false, error.localizedDescription)
        }
    }

    func setKillSwitch(
        enabled: Bool,
        interface: String,
        allowedServerHosts: [String],
        withReply rawReply: @escaping (Bool, String?) -> Void
    ) {
        let reply = Self.once(rawReply)
        do {
            try PacketFilterKillSwitch.methodClient.setEnabled(
                enabled,
                interface: interface,
                allowedServerHosts: allowedServerHosts
            )
            reply(true, nil)
        } catch {
            reply(false, error.localizedDescription)
        }
    }

    func stopTunnel(withReply rawReply: @escaping (Bool, String?) -> Void) {
        let reply = Self.once(rawReply)
        SingboxRunner.shared.stop()
        PacketFilterKillSwitch.methodClient.disable()
        // Отвечаем правдой: если процесс всё ещё жив (не поддался даже
        // SIGKILL), клиент обязан узнать об этом, а не увидеть «отключено».
        let stillRunning = SingboxRunner.shared.isRunning
        if stillRunning {
            os_log("stopTunnel: ядро не остановилось", log: logger, type: .error)
        }
        reply(!stillRunning, stillRunning ? "Ядро sing-box не остановилось" : nil)
    }

    func reconnectTunnel(withReply rawReply: @escaping (Bool, String?) -> Void) {
        let reply = Self.once(rawReply)
        do {
            try SingboxRunner.shared.reconnect()
            reply(true, nil)
        } catch {
            os_log("reconnectTunnel: %{public}@", log: logger, type: .error, error.localizedDescription)
            reply(false, error.localizedDescription)
        }
    }

    func isTunnelRunning(withReply reply: @escaping (Bool) -> Void) {
        reply(SingboxRunner.shared.isRunning)
    }

    private func resolveSingBoxURL() -> URL? {
        guard let helperExecutable = Bundle.main.executableURL else { return nil }
        return AppBundleLocator.singBoxURL(from: helperExecutable)
    }
}

class HelperDelegate: NSObject, NSXPCListenerDelegate {
    func listener(_ listener: NSXPCListener, shouldAcceptNewConnection newConnection: NSXPCConnection) -> Bool {
        // Без этой проверки root-демон выполнял startTunnel для ЛЮБОГО
        // локального процесса, то есть заворачивал весь трафик машины куда
        // угодно по просьбе кого угодно. Подробности отбора — в XPCClientValidator.
        guard XPCClientValidator.allows(newConnection) else { return false }
        newConnection.exportedInterface = NSXPCInterface(with: MethodVPNHelperProtocol.self)
        newConnection.exportedObject = HelperService()
        newConnection.resume()
        return true
    }
}
