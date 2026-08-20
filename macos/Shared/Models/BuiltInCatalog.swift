import Foundation

/// Метаданные сервера Method Network для UI.
public struct ServerCatalogEntry: Codable, Identifiable, Sendable, Equatable {
    public var id: UUID
    public var name: String
    public var flag: String
    public var city: String
    public var profile: ServerProfile

    private enum CodingKeys: String, CodingKey {
        case id, name, flag, city
        case proto = "protocol"
        case host, port, parameters
    }

    public init(id: UUID, name: String, flag: String, city: String, profile: ServerProfile) {
        self.id = id
        self.name = name
        self.flag = flag
        self.city = city
        self.profile = profile
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        name = try c.decode(String.self, forKey: .name)
        flag = try c.decode(String.self, forKey: .flag)
        city = try c.decode(String.self, forKey: .city)
        let protoRaw = try c.decode(String.self, forKey: .proto)
        guard let proto = VPNProtocol(rawValue: protoRaw) else {
            throw DecodingError.dataCorruptedError(
                forKey: .proto, in: c,
                debugDescription: "неизвестный протокол «\(protoRaw)»")
        }
        let host = try c.decode(String.self, forKey: .host)
        let port = try c.decode(Int.self, forKey: .port)
        let parameters = try c.decode(ServerProfile.Parameters.self, forKey: .parameters)
        profile = ServerProfile(id: id, name: name, protocol: proto, host: host, port: port, parameters: parameters)
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(name, forKey: .name)
        try c.encode(flag, forKey: .flag)
        try c.encode(city, forKey: .city)
        try c.encode(profile.protocol.rawValue, forKey: .proto)
        try c.encode(profile.host, forKey: .host)
        try c.encode(profile.port, forKey: .port)
        try c.encode(profile.parameters, forKey: .parameters)
    }
}

/// Встроенный каталог серверов Method Network (Resources/servers.json).
///
/// **Проверка идёт по записи, а не по файлу.** Раньше валидировался весь файл
/// целиком, и первая же непонятная запись бросала исключение — каталог не
/// загружался вовсе, приложение показывало «Ошибка чтения servers.json» и
/// оставалось без единого сервера. Один кривой gRPC-профиль так уже ронял
/// весь список. Теперь битая или незнакомая запись пропускается с записанной
/// причиной, а остальной каталог живёт.
///
/// Отказаться грузить каталог целиком можно только в трёх случаях: файла нет,
/// JSON не разбирается / нет обязательных полей верхнего уровня, либо после
/// отсева не осталось ни одной годной записи или на неё не разрешается
/// `defaultServerID`.
///
/// Причины пропуска не прячутся в лог: они лежат в `Payload.skipped` — человек
/// должен иметь возможность узнать, почему сервер исчез из списка. Молчаливая
/// потеря записи так же недопустима, как молчаливый отказ, выданный за успех.
public enum BuiltInCatalog {

    /// Предел длины каталога. Больше — почти наверняка не наш файл.
    public static let maxServers = 64

    /// Запись, которая не попала в каталог, и причина.
    public struct SkippedEntry: Codable, Sendable, Equatable, Identifiable {
        /// Порядковый номер записи в файле, с нуля.
        public var index: Int
        /// `id` из файла, если его удалось прочитать.
        public var rawID: String?
        /// `name` из файла, если его удалось прочитать.
        public var name: String?
        /// Человекочитаемая причина — её показываем в интерфейсе.
        public var reason: String

        public var id: String { rawID ?? "#\(index)" }

        /// Как назвать запись в интерфейсе, если имени в файле не оказалось.
        public var displayName: String {
            if let name, !name.isEmpty { return name }
            if let rawID, !rawID.isEmpty { return rawID }
            return "запись №\(index + 1)"
        }

        /// Строка для списка в интерфейсе и для лога.
        public var description: String { "\(displayName): \(reason)" }

        public init(index: Int, rawID: String?, name: String?, reason: String) {
            self.index = index
            self.rawID = rawID
            self.name = name
            self.reason = reason
        }
    }

    public struct Payload: Codable, Sendable {
        public var version: Int
        public var defaultServerID: UUID
        public var servers: [ServerCatalogEntry]
        /// Записи, потерянные при чтении. В норме пусто.
        public var skipped: [SkippedEntry]

        public init(
            version: Int,
            defaultServerID: UUID,
            servers: [ServerCatalogEntry],
            skipped: [SkippedEntry] = []
        ) {
            self.version = version
            self.defaultServerID = defaultServerID
            self.servers = servers
            self.skipped = skipped
        }

        private enum CodingKeys: String, CodingKey {
            case version, defaultServerID, servers, skipped
        }

        /// Разбор мягкий: годные записи собираются, негодные откладываются в
        /// `skipped`. Логика живёт здесь, а не в `load`, чтобы прямой
        /// `JSONDecoder().decode(Payload.self, …)` (так делают скрипты в
        /// MacOS/Scripts) вёл себя ровно так же, как приложение.
        public init(from decoder: Decoder) throws {
            // Все поля присваиваем в самом конце: пока self не собран целиком,
            // замыкание не может захватить его свойства.
            let c = try decoder.container(keyedBy: CodingKeys.self)
            let fileVersion = try c.decode(Int.self, forKey: .version)
            let defaultID = try c.decode(UUID.self, forKey: .defaultServerID)
            let raw = try c.decode([LenientEntry].self, forKey: .servers)

            var accepted: [ServerCatalogEntry] = []
            var problems: [SkippedEntry] = []
            var seenIDs = Set<UUID>()

            for (index, item) in raw.enumerated() {
                func skip(_ reason: String) {
                    problems.append(SkippedEntry(
                        index: index, rawID: item.rawID, name: item.name, reason: reason))
                }
                guard accepted.count < BuiltInCatalog.maxServers else {
                    skip("каталог длиннее \(BuiltInCatalog.maxServers) записей — лишнее отброшено")
                    continue
                }
                guard let entry = item.entry else {
                    skip(item.failure ?? "запись не разобралась")
                    continue
                }
                if let reason = BuiltInCatalog.validationFailure(for: entry) {
                    skip(reason)
                    continue
                }
                guard seenIDs.insert(entry.id).inserted else {
                    skip("такой id уже есть в каталоге")
                    continue
                }
                accepted.append(entry)
            }

            guard !accepted.isEmpty else {
                throw LoadError.noValidEntries(problems)
            }
            guard accepted.contains(where: { $0.id == defaultID }) else {
                throw LoadError.defaultServerUnresolved(id: defaultID, skipped: problems)
            }

            version = fileVersion
            defaultServerID = defaultID
            servers = accepted
            skipped = problems
        }
    }

    public enum LoadError: Error, LocalizedError {
        case missingFile
        case decodeFailed(String)
        case invalidPayload(String)
        /// После отсева не осталось ни одной годной записи.
        case noValidEntries([SkippedEntry])
        /// Сервер по умолчанию не разрешается ни в одну уцелевшую запись.
        case defaultServerUnresolved(id: UUID, skipped: [SkippedEntry])

        public var errorDescription: String? {
            switch self {
            case .missingFile:
                return """
                Файл servers.json не найден.
                Выполните: cd MacOS && python3 Scripts/fetch_server_profiles.py
                """
            case .decodeFailed(let msg):
                return "Ошибка чтения servers.json: \(msg)"
            case .invalidPayload(let msg):
                return "Некорректный каталог серверов: \(msg)"
            case .noValidEntries(let skipped):
                return "В каталоге нет ни одного пригодного сервера.\n"
                    + LoadError.list(skipped)
            case .defaultServerUnresolved(let id, let skipped):
                return "Сервер по умолчанию (\(id.uuidString)) отсутствует среди пригодных записей.\n"
                    + LoadError.list(skipped)
            }
        }

        private static func list(_ skipped: [SkippedEntry]) -> String {
            guard !skipped.isEmpty else { return "Причины неизвестны." }
            return skipped.map { "• \($0.description)" }.joined(separator: "\n")
        }
    }

    public static func load(from bundle: Bundle = .main) throws -> Payload {
        guard let url = bundle.url(forResource: "servers", withExtension: "json") else {
            throw LoadError.missingFile
        }
        do {
            let data = try Data(contentsOf: url)
            let payload = try JSONDecoder().decode(Payload.self, from: data)
            guard payload.version > 0 else {
                throw LoadError.invalidPayload("версия должна быть больше нуля")
            }
            return payload
        } catch let error as LoadError {
            throw error
        } catch {
            throw LoadError.decodeFailed(describe(error))
        }
    }

    // MARK: - Проверка одной записи

    /// Возвращает причину, по которой запись нельзя пускать в каталог, или nil.
    /// Смысл проверок тот же, что был раньше, — изменилась только цена отказа:
    /// теперь падает одна запись, а не весь файл.
    public static func validationFailure(for entry: ServerCatalogEntry) -> String? {
        let profile = entry.profile
        guard !entry.name.isEmpty, entry.name.count <= 96 else {
            return "пустое или слишком длинное имя"
        }
        guard !entry.city.isEmpty, entry.city.count <= 96 else {
            return "пустой или слишком длинный город"
        }
        guard isValidHost(profile.host) else { return "некорректный адрес узла" }
        guard (1...65_535).contains(profile.port) else { return "порт вне диапазона 1…65535" }

        switch (profile.protocol, profile.parameters) {
        case (.hysteria2, .hysteria2(let p)):
            guard !p.password.isEmpty, p.password.count <= 1_024 else {
                return "пустой или слишком длинный пароль Hysteria2"
            }
            guard !p.alpn.isEmpty, p.alpn.count <= 8 else {
                return "некорректный ALPN у Hysteria2"
            }
        case (.vlessReality, .vlessReality(let p)):
            guard UUID(uuidString: p.uuid) != nil else { return "uuid не похож на UUID" }
            guard !p.sni.isEmpty else { return "пустой sni у VLESS Reality" }
            guard !p.publicKey.isEmpty else { return "пустой publicKey у VLESS Reality" }
            guard !p.fingerprint.isEmpty else { return "пустой fingerprint у VLESS Reality" }
        case (.trojan, .trojan(let p)):
            guard !p.password.isEmpty, p.password.count <= 1_024 else {
                return "пустой или слишком длинный пароль Trojan"
            }
        case (.shadowsocks, .shadowsocks(let p)):
            guard !p.method.isEmpty else { return "не указан шифр Shadowsocks" }
            guard !p.password.isEmpty, p.password.count <= 1_024 else {
                return "пустой или слишком длинный пароль Shadowsocks"
            }
        default:
            return "протокол «\(profile.protocol.rawValue)» не совпадает с блоком parameters"
        }
        return nil
    }

    private static func isValidHost(_ host: String) -> Bool {
        guard !host.isEmpty, host.count <= 253 else { return false }
        return host.unicodeScalars.allSatisfy {
            !CharacterSet.whitespacesAndNewlines.contains($0)
                && !CharacterSet.controlCharacters.contains($0)
        }
    }

    // MARK: - Мягкая обёртка над записью

    /// Обёртка, чья `init(from:)` не бросает: ошибка разбора одной записи
    /// превращается в текст причины. Так элемент массива всё равно
    /// «съедается» декодером, и разбор продолжается со следующего.
    struct LenientEntry: Decodable {
        let entry: ServerCatalogEntry?
        let failure: String?
        let rawID: String?
        let name: String?

        private enum ProbeKeys: String, CodingKey { case id, name }

        init(from decoder: Decoder) throws {
            // Опознавательные поля тянем отдельно и мягко: даже у безнадёжно
            // битой записи человеку надо показать, какая именно пропала.
            var probedID: String?
            var probedName: String?
            if let probe = try? decoder.container(keyedBy: ProbeKeys.self) {
                probedID = try? probe.decode(String.self, forKey: .id)
                probedName = try? probe.decode(String.self, forKey: .name)
            }
            rawID = probedID
            name = probedName
            do {
                entry = try ServerCatalogEntry(from: decoder)
                failure = nil
            } catch {
                entry = nil
                failure = BuiltInCatalog.describe(error)
            }
        }
    }

    /// Человекочитаемое описание ошибки декодирования — вместо
    /// «The data couldn’t be read because it isn’t in the correct format».
    static func describe(_ error: Error) -> String {
        guard let error = error as? DecodingError else { return error.localizedDescription }
        func path(_ codingPath: [CodingKey]) -> String {
            let parts = codingPath.map { $0.intValue.map { "[\($0)]" } ?? $0.stringValue }
            return parts.isEmpty ? "" : " (\(parts.joined(separator: ".")))"
        }
        switch error {
        case .keyNotFound(let key, let ctx):
            return "нет поля «\(key.stringValue)»\(path(ctx.codingPath))"
        case .typeMismatch(let type, let ctx):
            return "поле не того типа, ожидался \(type)\(path(ctx.codingPath))"
        case .valueNotFound(let type, let ctx):
            return "нет значения типа \(type)\(path(ctx.codingPath))"
        case .dataCorrupted(let ctx):
            return "\(ctx.debugDescription)\(path(ctx.codingPath))"
        @unknown default:
            return error.localizedDescription
        }
    }
}
