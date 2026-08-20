import Foundation

/// Транспортный протокол сервера.
public enum VPNProtocol: String, Codable, Sendable, CaseIterable {
    case hysteria2
    case vlessReality = "vless-reality"
    case trojan
    case shadowsocks

    public var displayName: String {
        switch self {
        case .hysteria2:    return "Hysteria2"
        case .vlessReality: return "VLESS + Reality"
        case .trojan:       return "Trojan"
        case .shadowsocks:  return "Shadowsocks"
        }
    }
}

/// Профиль одного сервера. Хранит протокол-специфичные параметры в `parameters`,
/// что упрощает добавление новых протоколов без новой модели.
public struct ServerProfile: Codable, Identifiable, Sendable, Equatable {
    public var id: UUID
    public var name: String
    public var `protocol`: VPNProtocol
    public var host: String
    public var port: Int

    /// Параметры конкретного протокола. Доступ — через типизированные обёртки ниже.
    public var parameters: Parameters

    /// Метка выделения, которую провайдер поставил в имя узла: «VIP», «PREMIUM»,
    /// «⭐» и подобное. `nil` — обычный сервер.
    ///
    /// Почему именно из имени, а не из отдельного поля подписки: имя записи
    /// (фрагмент после `#` в share-ссылке) — единственный канал, который
    /// провайдер задаёт свободно в ЛЮБОЙ панели без её доработки. Заголовка
    /// «выдели этот узел» ни одна панель не шлёт, и выдумывать его бессмысленно —
    /// его некому выставить.
    ///
    /// Поле опциональное намеренно: модель общая с Android и Windows, старые
    /// каталоги его не содержат, а `nil` при кодировании вовсе не пишется —
    /// то есть для всех, кто про выделение не знает, JSON не изменился.
    public var featuredLabel: String?

    /// Сервер выделен провайдером.
    public var isFeatured: Bool { featuredLabel != nil }

    public init(
        id: UUID = UUID(),
        name: String,
        protocol proto: VPNProtocol,
        host: String,
        port: Int,
        parameters: Parameters,
        featuredLabel: String? = nil
    ) {
        self.id = id
        self.name = name
        self.protocol = proto
        self.host = host
        self.port = port
        self.parameters = parameters
        self.featuredLabel = featuredLabel
    }
}

extension ServerProfile {
    /// Объединение параметров протоколов. Кодируется как обычный объект —
    /// присутствуют только релевантные поля.
    public enum Parameters: Codable, Sendable, Equatable {
        case hysteria2(Hysteria2)
        case vlessReality(VLESSReality)
        case trojan(Trojan)
        case shadowsocks(Shadowsocks)

        // MARK: Hysteria2

        public struct Hysteria2: Codable, Sendable, Equatable {
            /// Пароль аутентификации (auth).
            public var password: String
            /// SNI для TLS (tls.server_name). Если nil — используется host.
            public var sni: String?
            /// Пропустить проверку сертификата (self-signed серверы).
            public var allowInsecure: Bool
            /// Salamander-обфускация: пароль. Если nil — обфускации нет.
            public var obfsPassword: String?
            /// Лимиты полосы (Мбит/с). 0/nil = без BBR-ограничения.
            public var upMbps: Int?
            public var downMbps: Int?
            /// ALPN, по умолчанию ["h3"].
            public var alpn: [String]
            /// Диапазон портов для прыжков, напр. "36000:50000". nil = ходить
            /// только на основной порт. Полоса обязана НЕ пересекаться с теми,
            /// что на нодах отданы alt-инстансу и TUIC: правила REDIRECT
            /// проверяются по порядку, и попадание в чужую полосу уводит трафик
            /// на инстанс с другим obfs-паролем — связь молча умирает до
            /// следующего прыжка.
            public var serverPorts: String?
            /// Как часто менять порт. nil = значение sing-box по умолчанию.
            public var hopInterval: String?

            public init(
                password: String,
                sni: String? = nil,
                allowInsecure: Bool = false,
                obfsPassword: String? = nil,
                upMbps: Int? = nil,
                downMbps: Int? = nil,
                alpn: [String] = ["h3"],
                serverPorts: String? = nil,
                hopInterval: String? = nil
            ) {
                self.password = password
                self.sni = sni
                self.allowInsecure = allowInsecure
                self.obfsPassword = obfsPassword
                self.upMbps = upMbps
                self.downMbps = downMbps
                self.alpn = alpn
                self.serverPorts = serverPorts
                self.hopInterval = hopInterval
            }
        }

        // MARK: VLESS + Reality

        public struct VLESSReality: Codable, Sendable, Equatable {
            /// UUID пользователя.
            public var uuid: String
            /// flow, обычно "xtls-rprx-vision". Пусто = без flow.
            public var flow: String?
            /// TLS SNI / маскируемый домен (server_name).
            public var sni: String
            /// Reality публичный ключ (pbk).
            public var publicKey: String
            /// Reality short_id (sid). Может быть пустым.
            public var shortID: String
            /// uTLS fingerprint (fp), напр. "chrome".
            public var fingerprint: String
            /// Имя gRPC-сервиса (serviceName в ссылке). Заполнено — значит транспорт
            /// gRPC, пусто или nil — обычный TCP с Vision.
            ///
            /// gRPC добавлен потому, что по замерам он единственный отработал на
            /// мобильном канале три ноды из трёх: у него HTTP/2-фрейминг, пакеты
            /// мельче и форма трафика непохожа на голый TLS. Vision при этом
            /// быстрее там, где сеть не мешает, поэтому оба нужны.
            public var grpcServiceName: String?

            public init(
                uuid: String,
                flow: String? = "xtls-rprx-vision",
                sni: String,
                publicKey: String,
                shortID: String = "",
                fingerprint: String = "chrome",
                grpcServiceName: String? = nil
            ) {
                self.uuid = uuid
                self.flow = flow
                self.sni = sni
                self.publicKey = publicKey
                self.shortID = shortID
                self.fingerprint = fingerprint
                self.grpcServiceName = grpcServiceName
            }
        }

        // MARK: Trojan

        public struct Trojan: Codable, Sendable, Equatable {
            /// Пароль. В Trojan он же и есть аутентификация — отдельного
            /// идентификатора пользователя нет.
            public var password: String
            /// SNI. Для Trojan он обязан совпадать с именем в сертификате:
            /// протокол держится на ПОДЛИННОСТИ TLS, а не на подделке, как
            /// Reality. Несовпадение = соединение не встанет.
            public var sni: String?
            /// Пропустить проверку сертификата. Для самоподписанных.
            public var allowInsecure: Bool
            /// uTLS fingerprint. Пусто = обычный TLS-стек Go.
            public var fingerprint: String?
            /// Имя gRPC-сервиса, если транспорт gRPC. Пусто = голый TCP.
            public var grpcServiceName: String?
            /// Путь WebSocket, если транспорт ws. Пусто = не ws.
            public var wsPath: String?
            /// Host-заголовок для WebSocket.
            public var wsHost: String?

            public init(
                password: String,
                sni: String? = nil,
                allowInsecure: Bool = false,
                fingerprint: String? = nil,
                grpcServiceName: String? = nil,
                wsPath: String? = nil,
                wsHost: String? = nil
            ) {
                self.password = password
                self.sni = sni
                self.allowInsecure = allowInsecure
                self.fingerprint = fingerprint
                self.grpcServiceName = grpcServiceName
                self.wsPath = wsPath
                self.wsHost = wsHost
            }
        }

        // MARK: Shadowsocks

        public struct Shadowsocks: Codable, Sendable, Equatable {
            /// Шифр, например `2022-blake3-aes-128-gcm` или `aes-256-gcm`.
            /// Имя передаётся ядру как есть: список поддерживаемых у sing-box
            /// свой, и подменять его догадками нельзя.
            public var method: String
            /// Пароль. Для методов семейства 2022 это base64-ключ нужной длины,
            /// и портить его нормализацией нельзя.
            public var password: String

            public init(method: String, password: String) {
                self.method = method
                self.password = password
            }
        }
    }
}

extension ServerProfile.Parameters {
    /// Кодирование с тегом протокола, чтобы декодирование было однозначным.
    private enum CodingKeys: String, CodingKey { case kind, hysteria2, vlessReality, trojan, shadowsocks }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let kind = try c.decode(String.self, forKey: .kind)
        switch kind {
        case "hysteria2":
            self = .hysteria2(try c.decode(Hysteria2.self, forKey: .hysteria2))
        case "vless-reality":
            self = .vlessReality(try c.decode(VLESSReality.self, forKey: .vlessReality))
        case "trojan":
            self = .trojan(try c.decode(Trojan.self, forKey: .trojan))
        case "shadowsocks":
            self = .shadowsocks(try c.decode(Shadowsocks.self, forKey: .shadowsocks))
        default:
            throw DecodingError.dataCorruptedError(
                forKey: .kind, in: c, debugDescription: "Unknown protocol kind: \(kind)")
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .hysteria2(let p):
            try c.encode("hysteria2", forKey: .kind)
            try c.encode(p, forKey: .hysteria2)
        case .vlessReality(let p):
            try c.encode("vless-reality", forKey: .kind)
            try c.encode(p, forKey: .vlessReality)
        case .trojan(let p):
            try c.encode("trojan", forKey: .kind)
            try c.encode(p, forKey: .trojan)
        case .shadowsocks(let p):
            try c.encode("shadowsocks", forKey: .kind)
            try c.encode(p, forKey: .shadowsocks)
        }
    }
}
