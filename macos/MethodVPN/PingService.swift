import Foundation

/// Замер реального ICMP round-trip до сервера.
/// Привязка к en0 не даёт стороннему Packet Tunnel вернуть локальные «2 ms».
enum PingService {
    /// Возвращает задержку в мс или nil при таймауте/ошибке.
    static func measure(host: String, port _: UInt16, timeout: TimeInterval = 2.0) async -> Int? {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/sbin/ping")
                process.arguments = [
                    "-b", "en0",
                    "-n", "-q",
                    "-c", "2",
                    "-W", String(Int(timeout * 1_000)),
                    host,
                ]

                let pipe = Pipe()
                process.standardOutput = pipe
                process.standardError = pipe
                do {
                    try process.run()
                    process.waitUntilExit()
                    let data = pipe.fileHandleForReading.readDataToEndOfFile()
                    let output = String(data: data, encoding: .utf8) ?? ""
                    continuation.resume(returning: parseAverageRTT(output))
                } catch {
                    continuation.resume(returning: nil)
                }
            }
        }
    }

    private static func parseAverageRTT(_ output: String) -> Int? {
        guard let statsLine = output.split(separator: "\n").first(where: {
            $0.contains("min/avg/max") && $0.contains("=")
        }),
        let metrics = statsLine.split(separator: "=", maxSplits: 1).last?
            .trimmingCharacters(in: .whitespaces)
            .split(separator: " ").first else { return nil }

        let values = metrics.split(separator: "/")
        guard values.count >= 2, let average = Double(values[1]) else { return nil }
        return Int(average.rounded())
    }
}
