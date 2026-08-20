import Foundation

/// Разбор маркера выделения из имени узла.
///
/// **Почему из имени.** Панель отдаёт подписку — список share-ссылок и
/// несколько заголовков на всю подписку целиком (`profile-title`, `announce`,
/// `profile-update-interval`…). Заголовка «выдели вот этот узел» не шлёт ни
/// одна панель, да и не может: заголовок относится к подписке, а не к строке
/// в ней. Единственное, что провайдер задаёт свободно ПОУЗЛОВО и в любой
/// панели без доработки, — имя записи, то есть фрагмент после `#`.
/// В живой подписке это, например, `🇺🇸 NOT_A_RKN-US · gRPC`.
///
/// Поэтому маркер читаем из имени и поддерживаем сразу несколько
/// распространённых форм, а не одну свою: провайдер уже как-то называет узлы,
/// и подстраиваться должен клиент.
///
/// Распознанный маркер из имени **вычищается**: если оставить его на месте,
/// список превратится в кашу из значков, а рядом будет ещё и наш собственный.
public enum ServerNameMarker {

    /// Символьные маркеры. Набор намеренно узкий: чем шире, тем чаще
    /// декоративная эмодзи в имени случайно делает узел «золотым».
    /// Флаги стран (🇺🇸, 🇫🇮) сюда не входят и никогда не вырезаются.
    private static let symbols: Set<Unicode.Scalar> = [
        "\u{2B50}",   // ⭐
        "\u{1F31F}",  // 🌟
        "\u{2728}",   // ✨
        "\u{1F451}",  // 👑
        "\u{1F48E}",  // 💎
        "\u{1F3C6}",  // 🏆
        "\u{1F947}",  // 🥇
        "\u{2605}",   // ★
        "\u{2606}",   // ☆
        "\u{2726}",   // ✦
        "\u{272A}",   // ✪
        "\u{2730}",   // ✰
    ]

    /// Словесные маркеры. Ищутся по границе слова и без учёта регистра,
    /// в скобках любого вида и без них: `[VIP]`, `(Premium)`, `【VIP】`,
    /// `US · GOLD`, `Премиум`.
    private static let words = [
        "vip", "premium", "prem", "gold", "pro",
        "премиум", "вип", "голд",
    ]

    private static let wordRegex: NSRegularExpression? = {
        let alt = words.joined(separator: "|")
        // Открывающая скобка, слово по границе, необязательный «+», скобка.
        let pattern = "(?:[\\[\\(\\{【〔<]\\s*)?\\b(?:\(alt))\\b\\+?(?:\\s*[\\]\\)\\}】〕>])?"
        return try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }()

    /// Пустые скобки и висящие разделители, остающиеся после вырезания маркера.
    private static let litterRegex = try? NSRegularExpression(
        pattern: "[\\[\\(\\{【〔<]\\s*[\\]\\)\\}】〕>]")

    /// Разобрать имя: вернуть очищенное имя и метку выделения (nil — обычный сервер).
    public static func strip(_ raw: String) -> (name: String, label: String?) {
        let original = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !original.isEmpty else { return (original, nil) }

        var label: String? = nil
        var work = original

        // 1. Словесный маркер. Он информативнее символа, поэтому имеет приоритет
        //    при выборе того, что показать человеку.
        if let regex = wordRegex {
            let range = NSRange(work.startIndex..., in: work)
            if let m = regex.firstMatch(in: work, range: range),
               let r = Range(m.range, in: work) {
                label = work[r]
                    .trimmingCharacters(in: CharacterSet(charactersIn: "[](){}【】〔〕<> \t+"))
                    .uppercased()
            }
            work = regex.stringByReplacingMatches(
                in: work, range: NSRange(work.startIndex..., in: work), withTemplate: " ")
        }

        // 2. Символьные маркеры — посимвольно, чтобы не зависеть от того,
        //    записана эмодзи с селектором начертания (U+FE0F) или без него.
        var kept = ""
        for ch in work {
            if isSymbolMarker(ch) {
                if label == nil { label = String(ch.unicodeScalars.first.map(String.init) ?? "") }
                kept.append(" ")
            } else {
                kept.append(ch)
            }
        }
        work = kept

        guard label != nil else { return (original, nil) }

        // 3. Уборка: пустые скобки, сдвоенные пробелы, висящие разделители.
        if let litter = litterRegex {
            work = litter.stringByReplacingMatches(
                in: work, range: NSRange(work.startIndex..., in: work), withTemplate: " ")
        }
        work = work.split(separator: " ", omittingEmptySubsequences: true).joined(separator: " ")
        work = work.trimmingCharacters(in: CharacterSet(charactersIn: " \t-–—·|,:;/"))
        work = work.split(separator: " ", omittingEmptySubsequences: true).joined(separator: " ")

        // Имя, состоявшее из одного маркера, чистить не во что — лучше показать
        // как есть, чем пустую строку.
        return (work.isEmpty ? original : work, label)
    }

    private static func isSymbolMarker(_ ch: Character) -> Bool {
        var scalars = Array(ch.unicodeScalars)
        guard let first = scalars.first, symbols.contains(first) else { return false }
        scalars.removeFirst()
        // Допускаем только селекторы начертания: «⭐️» и «⭐» — одно и то же.
        return scalars.allSatisfy { $0.value == 0xFE0F || $0.value == 0xFE0E }
    }
}

/// Парсер share-ссылок в `ServerProfile`.
///
/// Поддерживается:
///   hysteria2://<password>@host:port?sni=&obfs=salamander&obfs-password=&insecure=1#name
///   vless://<uuid>@host:port?security=reality&pbk=&sid=&fp=chrome&sni=&flow=xtls-rprx-vision#name
///   trojan://<password>@host:port?sni=&type=tcp|grpc|ws&fp=#name
///   ss://base64(method:password)@host:port#name   (и старая форма целиком в base64)
public enum ShareLinkParser {

    public enum ParseError: Error, LocalizedError, Equatable {
        case emptyInput
        case unsupportedScheme(String)
        case unsupportedTransport(String)
        case malformedURL
        case missingField(String)

        public var errorDescription: String? {
            switch self {
            case .emptyInput:               return "Пустая ссылка"
            case .unsupportedScheme(let s): return "Неподдерживаемый протокол: \(s)"
            case .unsupportedTransport(let t):
                return "Транспорт «\(t)» не поддерживается. Доступны tcp, grpc и ws."
            case .malformedURL:             return "Некорректный формат ссылки"
            case .missingField(let f):      return "Отсутствует обязательное поле: \(f)"
            }
        }
    }

    /// Разобрать одну ссылку.
    public static func parse(_ raw: String) throws -> ServerProfile {
        let trimmed = stripPortHopping(raw.trimmingCharacters(in: .whitespacesAndNewlines))
        guard !trimmed.isEmpty else { throw ParseError.emptyInput }

        // Shadowsocks разбираем до URLComponents: у него есть историческая форма
        // ss://base64(method:password@host:port), где после схемы идёт сплошной
        // base64 и URLComponents на нём спотыкается.
        if trimmed.lowercased().hasPrefix("ss://") {
            return try parseShadowsocks(trimmed)
        }

        guard let comps = URLComponents(string: trimmed), let scheme = comps.scheme?.lowercased()
        else { throw ParseError.malformedURL }

        switch scheme {
        case "hysteria2", "hy2": return try parseHysteria2(comps)
        case "vless":            return try parseVLESS(comps)
        case "trojan":           return try parseTrojan(comps)
        default:                 throw ParseError.unsupportedScheme(scheme)
        }
    }

    /// Разобрать несколько ссылок (по строке на каждую), пропуская пустые и строки
    /// с `#` в начале (метаданные подписки — profile-title/announce/ping-type и т.п.,
    /// не share-ссылки). Возвращает успешно разобранные профили и ошибки по индексу.
    public static func parseMany(_ raw: String) -> (profiles: [ServerProfile], errors: [(line: Int, error: Error)]) {
        var profiles: [ServerProfile] = []
        var errors: [(Int, Error)] = []
        for (i, line) in raw.split(whereSeparator: \.isNewline).enumerated() {
            let s = line.trimmingCharacters(in: .whitespaces)
            guard !s.isEmpty, !s.hasPrefix("#") else { continue }
            do { profiles.append(try parse(s)) }
            catch { errors.append((i, error)) }
        }
        return (profiles, errors)
    }

    /// Hysteria2 умеет "port hopping" — сервер объявляет диапазон UDP-портов для обхода
    /// DPI/QoS по портам (NAT/firewall пробрасывает весь диапазон на реальный порт).
    /// В share-ссылке это пишется как `host:PORT,START-END`, что делает authority
    /// невалидным для `URLComponents` (весь URL не парсится, возвращает nil). Обрезаем
    /// диапазон, оставляя базовый порт — сервер и так слушает именно на нём напрямую.
    private static func stripPortHopping(_ raw: String) -> String {
        guard let regex = try? NSRegularExpression(pattern: #":(\d{1,5}),\d{1,5}-\d{1,5}"#) else { return raw }
        let range = NSRange(raw.startIndex..., in: raw)
        return regex.stringByReplacingMatches(in: raw, range: range, withTemplate: ":$1")
    }

    // MARK: - Hysteria2

    private static func parseHysteria2(_ c: URLComponents) throws -> ServerProfile {
        guard let host = c.host, !host.isEmpty else { throw ParseError.missingField("host") }
        let port = c.port ?? 443
        // Пароль — в user-info (может быть только user, без password).
        let password = decodeUserInfo(c)
        guard !password.isEmpty else { throw ParseError.missingField("password") }

        let q = queryDict(c)
        let obfsType = q["obfs"]?.lowercased()
        let obfsPassword = (obfsType == "salamander") ? q["obfs-password"] : nil

        let params = ServerProfile.Parameters.Hysteria2(
            password: password,
            sni: q["sni"] ?? q["peer"],
            allowInsecure: boolFlag(q["insecure"]),
            obfsPassword: emptyToNil(obfsPassword),
            upMbps: q["upmbps"].flatMap(Int.init),
            downMbps: q["downmbps"].flatMap(Int.init),
            alpn: q["alpn"].map { $0.split(separator: ",").map(String.init) } ?? ["h3"]
        )
        let named = naming(c, host: host)
        return ServerProfile(
            name: named.name,
            protocol: .hysteria2,
            host: host,
            port: port,
            parameters: .hysteria2(params),
            featuredLabel: named.label
        )
    }

    // MARK: - VLESS + Reality

    private static func parseVLESS(_ c: URLComponents) throws -> ServerProfile {
        guard let host = c.host, !host.isEmpty else { throw ParseError.missingField("host") }
        guard let port = c.port else { throw ParseError.missingField("port") }
        let uuid = decodeUserInfo(c)
        guard !uuid.isEmpty else { throw ParseError.missingField("uuid") }

        let q = queryDict(c)
        guard q["security"]?.lowercased() == "reality" else {
            throw ParseError.missingField("security=reality")
        }
        guard let pbk = q["pbk"], !pbk.isEmpty else { throw ParseError.missingField("pbk (public key)") }
        guard let sni = q["sni"] ?? q["peer"], !sni.isEmpty else { throw ParseError.missingField("sni") }

        // Транспорт. Раньше `type` не читался вовсе, и gRPC-ссылка молча
        // разбиралась как голый TCP: запись добавлялась, выглядела рабочей и
        // не подключалась никогда. Подписка раздаёт `*_grpc` с 27 июля,
        // так что молчаливая поломка успела стать штатной.
        let transport = (q["type"] ?? "tcp").lowercased()
        let service = emptyToNil(q["serviceName"] ?? q["servicename"])

        switch transport {
        case "tcp", "":
            break
        case "grpc":
            guard service != nil else { throw ParseError.missingField("serviceName (для gRPC)") }
        default:
            // Лучше честный отказ, чем добавленная запись, которая не работает.
            throw ParseError.unsupportedTransport(transport)
        }

        let params = ServerProfile.Parameters.VLESSReality(
            uuid: uuid,
            // gRPC несовместим с flow: Vision работает только поверх голого TCP,
            // вместе они дают отказ аутентификации на стороне Xray.
            flow: transport == "grpc" ? nil : emptyToNil(q["flow"]),
            sni: sni,
            publicKey: pbk,
            shortID: q["sid"] ?? "",
            fingerprint: q["fp"] ?? "chrome",
            grpcServiceName: service
        )
        let named = naming(c, host: host)
        return ServerProfile(
            name: named.name,
            protocol: .vlessReality,
            host: host,
            port: port,
            parameters: .vlessReality(params),
            featuredLabel: named.label
        )
    }

    // MARK: - Trojan

    /// `trojan://<password>@host:port?security=tls&sni=&type=tcp|grpc|ws&fp=#name`
    ///
    /// Trojan держится на ПОДЛИННОСТИ TLS: сервер предъявляет настоящий
    /// сертификат, а клиент без правильного пароля уходит на запасной сайт.
    /// Поэтому `sni` тут не маскировка, как в Reality, а обязательное поле —
    /// оно должно совпадать с именем в сертификате.
    private static func parseTrojan(_ c: URLComponents) throws -> ServerProfile {
        guard let host = c.host, !host.isEmpty else { throw ParseError.missingField("host") }
        let port = c.port ?? 443
        let password = decodeUserInfo(c)
        guard !password.isEmpty else { throw ParseError.missingField("password") }

        let q = queryDict(c)
        let transport = (q["type"] ?? "tcp").lowercased()
        var service: String? = nil
        var wsPath: String? = nil
        var wsHost: String? = nil

        switch transport {
        case "tcp", "":
            break
        case "grpc":
            service = emptyToNil(q["servicename"])
            guard service != nil else { throw ParseError.missingField("serviceName (для gRPC)") }
        case "ws":
            wsPath = emptyToNil(q["path"]) ?? "/"
            wsHost = emptyToNil(q["host"])
        default:
            // Честный отказ вместо записи, которая добавится и не заработает.
            throw ParseError.unsupportedTransport(transport)
        }

        let params = ServerProfile.Parameters.Trojan(
            password: password,
            sni: emptyToNil(q["sni"] ?? q["peer"]) ?? host,
            allowInsecure: boolFlag(q["allowinsecure"] ?? q["insecure"]),
            fingerprint: emptyToNil(q["fp"]),
            grpcServiceName: service,
            wsPath: wsPath,
            wsHost: wsHost
        )
        let named = naming(c, host: host)
        return ServerProfile(
            name: named.name,
            protocol: .trojan,
            host: host,
            port: port,
            parameters: .trojan(params),
            featuredLabel: named.label
        )
    }

    // MARK: - Shadowsocks

    /// Две несовместимые формы, обе встречаются в живых подписках:
    ///   SIP002:   ss://base64url(method:password)@host:port#name
    ///   старая:   ss://base64(method:password@host:port)#name
    ///
    /// Разбираем обе. Пароль НЕ нормализуем: у шифров семейства 2022 это
    /// base64-ключ фиксированной длины, и любая «чистка» его ломает.
    private static func parseShadowsocks(_ raw: String) throws -> ServerProfile {
        var body = String(raw.dropFirst("ss://".count))

        // Имя за решёткой отрезаем сразу: в старой форме оно не входит в base64.
        var name: String? = nil
        if let hash = body.firstIndex(of: "#") {
            name = String(body[body.index(after: hash)...]).removingPercentEncoding
            body = String(body[..<hash])
        }
        // Параметры плагина нам не нужны, но мешать разбору они не должны.
        if let q = body.firstIndex(of: "?") {
            body = String(body[..<q])
        }
        guard !body.isEmpty else { throw ParseError.malformedURL }

        var userInfo: String
        var hostPort: String

        if let at = body.lastIndex(of: "@") {
            // SIP002: слева base64 или открытый method:password, справа host:port.
            userInfo = String(body[..<at])
            hostPort = String(body[body.index(after: at)...])
            if !userInfo.contains(":"), let decoded = decodeBase64(userInfo) {
                userInfo = decoded
            }
        } else {
            // Старая форма: base64 закрывает всё целиком.
            guard let decoded = decodeBase64(body), let at = decoded.lastIndex(of: "@") else {
                throw ParseError.malformedURL
            }
            userInfo = String(decoded[..<at])
            hostPort = String(decoded[decoded.index(after: at)...])
        }

        guard let colon = userInfo.firstIndex(of: ":") else {
            throw ParseError.missingField("method:password")
        }
        let method = String(userInfo[..<colon])
        let password = String(userInfo[userInfo.index(after: colon)...])
        guard !method.isEmpty else { throw ParseError.missingField("method") }
        guard !password.isEmpty else { throw ParseError.missingField("password") }

        // Хост может быть IPv6 в скобках — тогда двоеточий много, и делить
        // надо по последнему.
        guard let portColon = hostPort.lastIndex(of: ":") else {
            throw ParseError.missingField("port")
        }
        var host = String(hostPort[..<portColon])
        host = host.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        guard let port = Int(hostPort[hostPort.index(after: portColon)...]), !host.isEmpty else {
            throw ParseError.missingField("host:port")
        }

        let params = ServerProfile.Parameters.Shadowsocks(method: method, password: password)
        // Маркер ищем только в имени: host — не поле провайдера, его чистить нечего.
        let marked = name.map(ServerNameMarker.strip)
        return ServerProfile(
            name: marked?.name ?? host,
            protocol: .shadowsocks,
            host: host,
            port: port,
            parameters: .shadowsocks(params),
            featuredLabel: marked?.label
        )
    }

    /// base64 из ссылок бывает и обычный, и url-safe, и без выравнивания.
    private static func decodeBase64(_ s: String) -> String? {
        var t = s.replacingOccurrences(of: "-", with: "+")
                 .replacingOccurrences(of: "_", with: "/")
        while t.count % 4 != 0 { t += "=" }
        guard let data = Data(base64Encoded: t) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    // MARK: - Helpers

    private static func queryDict(_ c: URLComponents) -> [String: String] {
        var d: [String: String] = [:]
        for item in c.queryItems ?? [] where item.value != nil {
            d[item.name.lowercased()] = item.value
        }
        return d
    }

    /// user-info часть URL (`user[:password]@`), percent-декодированная.
    private static func decodeUserInfo(_ c: URLComponents) -> String {
        var s = c.user ?? ""
        if let pw = c.password, !pw.isEmpty { s += ":" + pw }
        return s.removingPercentEncoding ?? s
    }

    /// Имя записи и метка выделения из фрагмента после `#`.
    /// Если имени нет — берём host, но маркер в нём не ищем: host задаёт не
    /// провайдер вручную, а инфраструктура.
    private static func naming(_ c: URLComponents, host: String) -> (name: String, label: String?) {
        guard let raw = fragmentName(c) else { return (host, nil) }
        return ServerNameMarker.strip(raw)
    }

    private static func fragmentName(_ c: URLComponents) -> String? {
        guard let f = c.fragment, !f.isEmpty else { return nil }
        return f.removingPercentEncoding ?? f
    }

    private static func boolFlag(_ v: String?) -> Bool {
        guard let v = v?.lowercased() else { return false }
        return v == "1" || v == "true" || v == "yes"
    }

    private static func emptyToNil(_ s: String?) -> String? {
        guard let s = s, !s.isEmpty else { return nil }
        return s
    }
}
