import Darwin
import Foundation

/// Управляет изолированным PF-anchor и не затрагивает правила macOS или других программ.
/// Вызывать только из root-helper: обычное приложение не имеет доступа к `/dev/pf`.
final class PacketFilterKillSwitch {
    static let methodVPN = PacketFilterKillSwitch(
        anchor: "com.apple/methodvpn",
        stateDirectory: URL(fileURLWithPath: "/Library/Application Support/MethodVPN")
    )
    static let methodClient = PacketFilterKillSwitch(
        anchor: "com.apple/methodclient",
        stateDirectory: URL(fileURLWithPath: "/Library/Application Support/Method")
    )

    private let anchor: String
    private let stateDirectory: URL
    private let tokenURL: URL
    private let queue: DispatchQueue

    private init(anchor: String, stateDirectory: URL) {
        self.anchor = anchor
        self.stateDirectory = stateDirectory
        self.tokenURL = stateDirectory.appendingPathComponent("pf-enable-token")
        self.queue = DispatchQueue(label: "\(anchor).killswitch")
    }

    /// Снимает оставшиеся после аварийного завершения правила и PF enable-reference.
    func recoverAfterUnexpectedExit() {
        queue.sync { disableLocked(ignoreErrors: true) }
    }

    func setEnabled(
        _ enabled: Bool,
        interface: String,
        allowedServerHosts: [String]
    ) throws {
        try queue.sync {
            if enabled {
                try enableLocked(interface: interface, allowedServerHosts: allowedServerHosts)
            } else {
                disableLocked(ignoreErrors: true)
            }
        }
    }

    func disable() {
        queue.sync { disableLocked(ignoreErrors: true) }
    }

    private func enableLocked(interface: String, allowedServerHosts: [String]) throws {
        disableLocked(ignoreErrors: true)
        guard interface.range(of: #"^[A-Za-z][A-Za-z0-9]{0,15}$"#, options: .regularExpression) != nil else {
            throw killSwitchError("Недопустимое имя сетевого интерфейса")
        }

        let hosts = Array(Set(allowedServerHosts)).sorted()
        guard !hosts.isEmpty, hosts.allSatisfy(isIPAddress) else {
            throw killSwitchError("Kill Switch принимает только IP-адреса VPN-нод")
        }

        let rules = buildRules(interface: interface, allowedServerHosts: hosts)
        try runPF(["-a", anchor, "-f", "-"], standardInput: rules)

        let enableOutput: String
        do {
            enableOutput = try runPF(["-E"])
        } catch {
            disableLocked(ignoreErrors: true)
            throw error
        }
        guard let token = parseToken(enableOutput) else {
            disableLocked(ignoreErrors: true)
            throw killSwitchError("pfctl не вернул enable-token")
        }

        do {
            try FileManager.default.createDirectory(
                at: stateDirectory,
                withIntermediateDirectories: true
            )
            try token.write(to: tokenURL, atomically: true, encoding: .utf8)
            chmod(tokenURL.path, S_IRUSR | S_IWUSR)
            // Старые direct-соединения не должны пережить включение блокирующих правил.
            try runPF(["-i", interface, "-F", "states"])
        } catch {
            disableLocked(ignoreErrors: true)
            throw error
        }
    }

    private func disableLocked(ignoreErrors: Bool) {
        do {
            // Сначала снимаем блокировку, затем уменьшаем PF reference count.
            try runPF(["-a", anchor, "-F", "rules"])
        } catch where !ignoreErrors {
            return
        } catch {}

        if let token = try? String(contentsOf: tokenURL, encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines),
           !token.isEmpty {
            _ = try? runPF(["-X", token])
        }
        try? FileManager.default.removeItem(at: tokenURL)
    }

    private func buildRules(interface: String, allowedServerHosts: [String]) -> String {
        var lines = [
            "pass out quick on lo0 all",
            "pass out quick on \(interface) inet proto udp from any port 68 to 255.255.255.255 port 67 keep state",
            "pass out quick on \(interface) inet from any to { 10.0.0.0/8, 100.64.0.0/10, 169.254.0.0/16, 172.16.0.0/12, 192.168.0.0/16, 224.0.0.0/4 } keep state",
            "pass out quick on \(interface) inet6 from any to { fc00::/7, fe80::/10, ff00::/8 } keep state",
        ]
        // Разрешение по ВЛАДЕЛЬЦУ СОКЕТА, а не по адресу назначения.
        //
        // Список адресов не работает по существу: у Trojan адрес доменный и
        // живёт за Cloudflare, чьи адреса меняются. Набор, закреплённый при
        // подключении, однажды перестаёт совпадать — и защита блокирует
        // собственный туннель. Владелец сокета не меняется никогда.
        //
        // Ядро запускается привилегированным демоном и работает от root,
        // поэтому правило пропускает root. Честная плата: наружу смогут
        // ходить и прочие системные службы, работающие от root. Для модели
        // угроз это приемлемо — защита существует против утечки трафика
        // ПОЛЬЗОВАТЕЛЬСКИХ приложений при упавшем туннеле, а системные службы
        // Apple на macOS в туннель не заворачиваются в принципе.
        //
        // Адреса узлов остаются в правилах дополнительно: если ядро однажды
        // будет запускаться не от root, защита не превратится в блокировку
        // всего разом, а продолжит пропускать хотя бы узлы.
        lines.append("pass out quick on \(interface) all user root keep state")
        for host in allowedServerHosts where isIPAddress(host) {
            let family = host.contains(":") ? "inet6" : "inet"
            lines.append("pass out quick on \(interface) \(family) from any to \(host) keep state")
        }
        lines.append("block drop out quick on \(interface) all")
        return lines.joined(separator: "\n") + "\n"
    }

    private func isIPAddress(_ value: String) -> Bool {
        var ipv4 = in_addr()
        if value.withCString({ inet_pton(AF_INET, $0, &ipv4) }) == 1 { return true }
        var ipv6 = in6_addr()
        return value.withCString { inet_pton(AF_INET6, $0, &ipv6) } == 1
    }

    private func parseToken(_ output: String) -> String? {
        guard let expression = try? NSRegularExpression(pattern: #"Token\s*:\s*(\d+)"#),
              let match = expression.firstMatch(
                in: output,
                range: NSRange(output.startIndex..., in: output)
              ),
              let range = Range(match.range(at: 1), in: output) else { return nil }
        return String(output[range])
    }

    @discardableResult
    private func runPF(_ arguments: [String], standardInput: String? = nil) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/sbin/pfctl")
        process.arguments = arguments
        let outputPipe = Pipe()
        process.standardOutput = outputPipe
        process.standardError = outputPipe

        var inputPipe: Pipe?
        if standardInput != nil {
            let pipe = Pipe()
            process.standardInput = pipe
            inputPipe = pipe
        }

        try process.run()
        if let standardInput, let inputPipe {
            inputPipe.fileHandleForWriting.write(Data(standardInput.utf8))
            try? inputPipe.fileHandleForWriting.close()
        }
        process.waitUntilExit()

        let data = outputPipe.fileHandleForReading.readDataToEndOfFile()
        let output = String(data: data, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard process.terminationStatus == 0 else {
            throw killSwitchError(output.isEmpty ? "pfctl завершился с ошибкой" : output)
        }
        return output
    }

    private func killSwitchError(_ message: String) -> NSError {
        NSError(
            domain: "PacketFilterKillSwitch",
            code: 1,
            userInfo: [NSLocalizedDescriptionKey: message]
        )
    }
}
