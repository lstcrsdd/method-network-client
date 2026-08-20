import Foundation

/// Управление полосами на ЖИВОМ ядре через его локальный API.
///
/// Это второй ярус конструкции. Первый — `LaneConfigBuilder` — описывает,
/// какие состояния вообще достижимы; здесь мы выбираем одно из них. Разделение
/// не косметическое: множество достижимых состояний задано статическим файлом
/// и проверено при сборке, поэтому что бы этот слой ни делал (и что бы ни
/// делал завладевший секретом посторонний процесс), вывести трафик за пределы
/// объявленного он не может — ядро отвечает `400`.
///
/// Ровно три операции меняют поведение без перезапуска ядра. Всё остальное —
/// состав маршрутов, инбаунды, TUN, DNS, правила — статика, и её смена стоит
/// пересоздания туннеля.
public actor LaneRuntime {

    public struct Endpoint: Sendable {
        public var controller: String
        public var secret: String

        public init(controller: String, secret: String) {
            self.controller = controller
            self.secret = secret
        }
    }

    public enum RuntimeError: Error, LocalizedError {
        case badResponse(Int, String)
        case notMember(lane: String, route: String)
        case unreachable(String)

        public var errorDescription: String? {
            switch self {
            case .badResponse(let code, let body):
                return "Ядро ответило \(code): \(body)"
            case .notMember(let lane, let route):
                return "Маршрут «\(route)» не входит в полосу «\(lane)» — ядро такого не разрешает"
            case .unreachable(let why):
                return "Ядро недоступно: \(why)"
            }
        }
    }

    private let endpoint: Endpoint
    private let session: URLSession

    public init(endpoint: Endpoint, session: URLSession = .shared) {
        self.endpoint = endpoint
        self.session = session
    }

    // MARK: - Чтение

    public struct LaneState: Sendable {
        public let tag: String
        /// Кто выбран сейчас.
        public let now: String
        /// Все допустимые члены. Это и есть множество достижимых состояний.
        public let members: [String]
    }

    /// Состояние всех полос и групп по осям.
    public func lanes() async throws -> [String: LaneState] {
        let json = try await get("/proxies")
        guard let proxies = json["proxies"] as? [String: Any] else { return [:] }
        var out: [String: LaneState] = [:]
        for (tag, raw) in proxies {
            guard let entry = raw as? [String: Any],
                  let type = entry["type"] as? String,
                  type.lowercased() == "selector" || type.lowercased() == "urltest",
                  let now = entry["now"] as? String else { continue }
            let members = (entry["all"] as? [String]) ?? []
            out[tag] = LaneState(tag: tag, now: now, members: members)
        }
        return out
    }

    /// Задержка конкретного маршрута, измеренная самим ядром.
    ///
    /// URL обязан быть `https://`. Ядро игнорирует `http://` и молча
    /// подставляет собственный адрес на gstatic — измерение уехало бы на
    /// Google, причём хост в РФ чувствительный, и метрика описывала бы не наш
    /// маршрут, а погоду.
    public func delay(route: String, url: String, timeoutMS: Int = 6000) async throws -> Int? {
        precondition(url.hasPrefix("https://"), "адрес пробы обязан быть https")
        let escaped = url.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? url
        let path = "/proxies/\(escape(route))/delay?timeout=\(timeoutMS)&url=\(escaped)"
        do {
            let json = try await get(path)
            return json["delay"] as? Int
        } catch RuntimeError.badResponse(let code, _) where code == 504 || code == 503 {
            // Не ошибка вызова, а честный отрицательный результат: маршрут не
            // ответил. Отличать это от «API сломан» обязательно, иначе
            // движок примет свою слепоту за смерть всех маршрутов.
            return nil
        }
    }

    // MARK: - Изменение

    /// Назначить полосе маршрут (или группу по оси).
    ///
    /// Живые соединения при этом НЕ рвутся — это асимметрия с
    /// автопереизбранием внутри группы, и она нам на руку: обрывать или дать
    /// дотечь становится решением политики, а не свойством конфига.
    public func select(lane: String, member: String) async throws {
        let (code, body) = try await put("/proxies/\(escape(lane))", ["name": member])
        switch code {
        case 200, 204:
            return
        case 400:
            // Ядро отвергло увод за пределы селектора. Это не сбой, а работа
            // защиты: множество достижимых состояний конечно.
            throw RuntimeError.notMember(lane: lane, route: member)
        default:
            throw RuntimeError.badResponse(code, body)
        }
    }

    /// Оборвать соединения полосы. Нужно при аварии и при ужесточении
    /// политики: решение о маршруте ядро принимает при ОТКРЫТИИ соединения,
    /// поэтому долгоживущий поток, начатый в разрешительном состоянии,
    /// переживёт переключение и продолжит течь по старому пути.
    @discardableResult
    public func drain(lane: String) async throws -> Int {
        let json = try await get("/connections")
        guard let list = json["connections"] as? [[String: Any]] else { return 0 }
        var dropped = 0
        for conn in list {
            guard let chains = conn["chains"] as? [String], chains.contains(lane),
                  let id = conn["id"] as? String else { continue }
            _ = try? await delete("/connections/\(escape(id))")
            dropped += 1
        }
        return dropped
    }

    /// Байты по маршрутам и полосам — из цепочек живых соединений.
    /// Пассивно и бесплатно: активная проба скорости жжёт трафик человека.
    public func throughputByChain() async throws -> [String: (up: Int64, down: Int64)] {
        let json = try await get("/connections")
        guard let list = json["connections"] as? [[String: Any]] else { return [:] }
        var out: [String: (up: Int64, down: Int64)] = [:]
        for conn in list {
            let up = (conn["upload"] as? NSNumber)?.int64Value ?? 0
            let down = (conn["download"] as? NSNumber)?.int64Value ?? 0
            for tag in (conn["chains"] as? [String]) ?? [] {
                var cur = out[tag] ?? (0, 0)
                cur.up += up
                cur.down += down
                out[tag] = cur
            }
        }
        return out
    }

    // MARK: - Транспорт

    private func escape(_ s: String) -> String {
        s.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? s
    }

    private func request(_ path: String, method: String, body: [String: Any]?) -> URLRequest? {
        guard let url = URL(string: "http://\(endpoint.controller)\(path)") else { return nil }
        var r = URLRequest(url: url, timeoutInterval: 10)
        r.httpMethod = method
        // Только заголовок: параметр в строке запроса ядро не принимает, а
        // секрет в URL попал бы в журналы.
        r.setValue("Bearer \(endpoint.secret)", forHTTPHeaderField: "Authorization")
        if let body {
            r.setValue("application/json", forHTTPHeaderField: "Content-Type")
            r.httpBody = try? JSONSerialization.data(withJSONObject: body)
        }
        return r
    }

    private func get(_ path: String) async throws -> [String: Any] {
        guard let r = request(path, method: "GET", body: nil) else {
            throw RuntimeError.unreachable("некорректный адрес")
        }
        let (data, resp) = try await session.data(for: r)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(code) else {
            throw RuntimeError.badResponse(code, String(data: data, encoding: .utf8) ?? "")
        }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
    }

    private func put(_ path: String, _ body: [String: Any]) async throws -> (Int, String) {
        guard let r = request(path, method: "PUT", body: body) else {
            throw RuntimeError.unreachable("некорректный адрес")
        }
        let (data, resp) = try await session.data(for: r)
        return ((resp as? HTTPURLResponse)?.statusCode ?? 0,
                String(data: data, encoding: .utf8) ?? "")
    }

    private func delete(_ path: String) async throws {
        guard let r = request(path, method: "DELETE", body: nil) else { return }
        _ = try await session.data(for: r)
    }
}
