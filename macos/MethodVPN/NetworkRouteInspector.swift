import Foundation

/// Проверяет системный маршрут до VPN-ноды до запуска собственного TUN.
/// Это позволяет заранее обнаружить попытку поднять Method поверх другого full-tunnel VPN.
enum NetworkRouteInspector {
    struct Route {
        let interface: String
        let gateway: String?

        var usesTunnel: Bool {
            interface.hasPrefix("utun") || interface.hasPrefix("ppp") || interface.hasPrefix("ipsec")
        }
    }

    static func route(to host: String) -> Route? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/sbin/route")
        process.arguments = ["-n", "get", host]

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = Pipe()

        do {
            try process.run()
            process.waitUntilExit()
        } catch {
            return nil
        }

        guard process.terminationStatus == 0 else { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        guard let output = String(data: data, encoding: .utf8) else { return nil }

        var interface: String?
        var gateway: String?
        for line in output.split(separator: "\n") {
            let parts = line.split(separator: ":", maxSplits: 1).map {
                $0.trimmingCharacters(in: .whitespaces)
            }
            guard parts.count == 2 else { continue }
            switch parts[0] {
            case "interface": interface = parts[1]
            case "gateway": gateway = parts[1]
            default: break
            }
        }

        guard let interface else { return nil }
        return Route(interface: interface, gateway: gateway)
    }

    /// Список VPN, подключённых через системный NetworkExtension/SCNetworkConnection.
    /// Собственный TUN Method создаётся sing-box напрямую и в этом списке не появляется.
    static func connectedSystemVPNNames() -> [String] {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/sbin/scutil")
        process.arguments = ["--nc", "list"]

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = Pipe()

        do {
            try process.run()
            process.waitUntilExit()
        } catch {
            return []
        }

        guard process.terminationStatus == 0,
              let output = String(
                data: pipe.fileHandleForReading.readDataToEndOfFile(),
                encoding: .utf8
              ) else { return [] }

        return output.split(separator: "\n").compactMap { line in
            guard line.contains("(Connected)") else { return nil }
            let quoted = line.split(separator: "\"", omittingEmptySubsequences: false)
            guard quoted.count >= 3 else { return "Системный VPN" }
            return String(quoted[quoted.count - 2])
        }
    }
}
