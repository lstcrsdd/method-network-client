import CryptoKit
import Foundation

/// Стабильный идентификатор устройства для панели подписок.
enum DeviceIdentity {
    static let hwid: String = {
        let source = platformUUID() ?? fallbackUUID()
        return source.uppercased()
    }()

    static let hashedHWID: String = {
        let digest = SHA256.hash(data: Data("method:\(hwid)".utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }()

    static var shortHWID: String {
        String(hwid.prefix(12)).uppercased()
    }

    static var deviceName: String {
        Host.current().localizedName ?? Host.current().name ?? "Mac"
    }

    private static func platformUUID() -> String? {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/sbin/ioreg")
        task.arguments = ["-rd1", "-c", "IOPlatformExpertDevice"]

        let pipe = Pipe()
        task.standardOutput = pipe
        task.standardError = Pipe()

        do {
            try task.run()
            task.waitUntilExit()
        } catch {
            return nil
        }

        guard task.terminationStatus == 0 else { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        guard let output = String(data: data, encoding: .utf8) else { return nil }

        for line in output.split(separator: "\n") {
            guard line.contains("IOPlatformUUID"),
                  let firstQuote = line.firstIndex(of: "\""),
                  let lastQuote = line.lastIndex(of: "\""),
                  firstQuote != lastQuote else { continue }
            let tail = line[line.index(after: firstQuote)..<lastQuote]
            if let valueStart = tail.lastIndex(of: "\"") {
                let value = tail[tail.index(after: valueStart)...]
                if !value.isEmpty { return String(value) }
            }
        }
        return nil
    }

    private static func fallbackUUID() -> String {
        let key = "method.device.fallbackUUID"
        if let stored = UserDefaults.standard.string(forKey: key), !stored.isEmpty {
            return stored
        }
        let generated = UUID().uuidString
        UserDefaults.standard.set(generated, forKey: key)
        return generated
    }
}
