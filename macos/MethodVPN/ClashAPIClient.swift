import Foundation

/// Клиент Clash API sing-box (управление + статистика трафика).
enum ClashAPIClient {
    struct TrafficSnapshot: Sendable {
        /// Суммарно переданные байты с момента старта ядра.
        var uploadTotal: Int64
        var downloadTotal: Int64
    }

    private static func request(path: String, controller: String, secret: String, timeout: TimeInterval) -> URLRequest? {
        guard let url = URL(string: "http://\(controller)\(path)") else { return nil }
        var request = URLRequest(url: url, timeoutInterval: timeout)
        request.setValue("Bearer \(secret)", forHTTPHeaderField: "Authorization")
        return request
    }

    static func ping(
        controller: String = "127.0.0.1:9090",
        secret: String = SingBoxConfigBuilder.defaultOptions.clashSecret
    ) async -> Bool {
        guard let request = request(path: "/version", controller: controller, secret: secret, timeout: 2) else { return false }
        do {
            let (_, response) = try await URLSession.shared.data(for: request)
            return (response as? HTTPURLResponse)?.statusCode == 200
        } catch {
            return false
        }
    }

    /// Снимок суммарного трафика через `/connections`.
    /// Скорость считается на стороне вызывающего как дельта между снимками.
    static func fetchTraffic(
        controller: String = "127.0.0.1:9090",
        secret: String = SingBoxConfigBuilder.defaultOptions.clashSecret
    ) async -> TrafficSnapshot? {
        guard let request = request(path: "/connections", controller: controller, secret: secret, timeout: 3) else { return nil }
        struct Response: Decodable {
            let downloadTotal: Int64
            let uploadTotal: Int64
        }
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
            let decoded = try JSONDecoder().decode(Response.self, from: data)
            return TrafficSnapshot(uploadTotal: decoded.uploadTotal, downloadTotal: decoded.downloadTotal)
        } catch {
            return nil
        }
    }
}
