import Foundation

/// Загрузка и разбор подписки. Панели (Remnawave/Marzban/3x-ui) часто отдают РАЗНЫЙ
/// формат в зависимости от User-Agent клиента: sing-box JSON, Xray JSON, Clash YAML
/// или base64-список share-ссылок. Пробуем несколько известных UA по порядку
/// надёжности — в первую очередь нативный sing-box (это наш же движок, меньше всего
/// потерь при разборе), затем откатываемся на обычные share-ссылки.
enum SubscriptionService {
    struct FetchResult {
        var profiles: [ServerProfile]
        var suggestedName: String?
        var trafficUsed: Int64?
        var trafficTotal: Int64?
        var expiresAt: Date?
        /// Ссылки, которые не удалось разобрать (неподдерживаемый транспорт и т.п.),
        /// но подписка в целом успешна — показываем это, а не молчим.
        var skippedCount: Int = 0
        /// Имя, которое подписка называет сама (заголовок `profile-title`,
        /// обычно в base64). Оно есть у большинства панелей, и человек знает
        /// подписку именно по нему, а не по домену.
        var title: String?
        /// Строка от провайдера (заголовок `announce`) — там бывает и
        /// поддержка, и предупреждение об истечении.
        var announce: String?
        /// Часы между автообновлениями (заголовок `profile-update-interval`).
        var updateIntervalHours: Int?
        /// Страница провайдера (`profile-web-page-url`) и канал поддержки
        /// (`support-url`). Панели шлют их рядом с announce; человеку нужны
        /// отдельные кнопки, а не строка, из которой надо копировать адрес.
        var webPageURL: URL?
        var supportURL: URL?
    }

    enum FetchError: Error, LocalizedError {
        case network(String)
        case empty
        case unsupportedFormat(String)
        /// Провайдер вернул понятное сообщение вместо серверов (например, «оплатите
        /// подписку» / «обратитесь к администратору») — частая практика: сообщение
        /// зашито прямо в имя фиктивного сервера, чтобы его увидел любой клиент.
        case providerMessage(String)

        var errorDescription: String? {
            switch self {
            case .network(let m):           return "Не удалось загрузить подписку: \(m)"
            case .empty:                    return "Подписка пуста или ссылки не распознаны"
            case .unsupportedFormat(let f): return "Формат подписки (\(f)) пока не поддерживается"
            case .providerMessage(let m):   return "Провайдер сообщает: «\(m)» — обратитесь к поставщику подписки"
            }
        }
    }

    private static let userAgentCandidates = [
        "Happ/2.4.5",
        "Happ",
        "SFA/1.11.0",
        "sing-box/1.13.0",
        "ClashMetaForAndroid/2.11.0",
        "v2rayNG/1.8.32",
        "Method/1.0",
    ]

    static func fetch(_ url: URL) async throws -> FetchResult {
        var lastError: Error = FetchError.empty
        for ua in userAgentCandidates {
            do {
                return try await fetchOnce(url, userAgent: ua)
            } catch let error as FetchError {
                lastError = error
                // Сообщение провайдера не зависит от UA — пробовать другой смысла нет.
                if case .providerMessage = error { throw error }
            } catch {
                lastError = error
            }
        }
        throw lastError
    }

    private static func fetchOnce(_ url: URL, userAgent: String) async throws -> FetchResult {
        var request = URLRequest(url: resolvedURL(url), timeoutInterval: 10)
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        request.setValue(DeviceIdentity.hwid, forHTTPHeaderField: "X-HWID")
        request.setValue(DeviceIdentity.hwid, forHTTPHeaderField: "HWID")
        request.setValue(DeviceIdentity.hwid, forHTTPHeaderField: "Hwid")
        request.setValue(DeviceIdentity.hwid, forHTTPHeaderField: "X-Method-HWID")
        request.setValue(DeviceIdentity.hwid, forHTTPHeaderField: "X-Device-HWID")
        request.setValue(DeviceIdentity.hwid, forHTTPHeaderField: "X-Client-HWID")
        request.setValue(DeviceIdentity.shortHWID, forHTTPHeaderField: "X-Device-ID")
        request.setValue(DeviceIdentity.deviceName, forHTTPHeaderField: "X-Device-Name")
        request.setValue("macOS", forHTTPHeaderField: "X-Device-Platform")
        request.setValue(DeviceIdentity.hashedHWID, forHTTPHeaderField: "X-Method-HWID-Hash")

        let data: Data
        let http: HTTPURLResponse
        do {
            let (d, response) = try await URLSession.shared.data(for: request)
            guard let h = response as? HTTPURLResponse else { throw FetchError.network("нет ответа") }
            data = d; http = h
        } catch let error as FetchError {
            throw error
        } catch {
            throw FetchError.network(error.localizedDescription)
        }
        if let htmlError = htmlErrorMessage(data), http.statusCode >= 500 {
            throw FetchError.network("HTTP \(http.statusCode): \(htmlError)")
        }
        guard 200..<300 ~= http.statusCode else { throw FetchError.network("HTTP \(http.statusCode)") }

        var profiles: [ServerProfile] = []
        var providerMessage: String?
        var skippedCount = 0

        if let outbounds = singBoxOutbounds(data) {
            for ob in outbounds {
                guard let type = ob["type"] as? String else { continue }
                if type == "vless", let p = vlessProfile(from: ob) {
                    profiles.append(p)
                } else if type == "hysteria2", let p = hysteria2Profile(from: ob) {
                    profiles.append(p)
                } else if type == "vless" || type == "hysteria2" {
                    // Есть тег, но не хватает обязательных полей (Reality/пароль) —
                    // либо заглушка провайдера, либо формат, который мы не разбираем.
                    if providerMessage == nil, let tag = ob["tag"] as? String { providerMessage = tag }
                    skippedCount += 1
                }
            }
        } else {
            let text = decodeBody(data)
            if let htmlError = htmlErrorMessage(data) { throw FetchError.network(htmlError) }
            let parsed = parseShareLinks(in: text)
            profiles = parsed.profiles
            skippedCount = parsed.errors.count
            if profiles.isEmpty, looksLikeYAML(text) {
                let clash = parseClashYAML(text)
                profiles = clash.profiles
                skippedCount += clash.skippedCount
            }
            if profiles.isEmpty, looksLikeXrayJSON(data) { throw FetchError.unsupportedFormat("Xray JSON") }
            if profiles.isEmpty { providerMessage = firstFragmentName(in: text) }
        }

        guard !profiles.isEmpty else {
            if let providerMessage { throw FetchError.providerMessage(providerMessage) }
            throw FetchError.empty
        }

        let name = http.value(forHTTPHeaderField: "Content-Disposition").flatMap(extractFilename)
        let (used, total, expires) = http.value(forHTTPHeaderField: "Subscription-Userinfo")
            .map(parseUserinfo) ?? (nil, nil, nil)
        let title = http.value(forHTTPHeaderField: "profile-title").flatMap(decodeHeaderText)
        let announce = http.value(forHTTPHeaderField: "announce").flatMap(decodeHeaderText)
        let interval = http.value(forHTTPHeaderField: "profile-update-interval").flatMap(Int.init)
        let web = http.value(forHTTPHeaderField: "profile-web-page-url").flatMap(safeHTTPURL)
        let support = (http.value(forHTTPHeaderField: "support-url")
            ?? http.value(forHTTPHeaderField: "announce-url")).flatMap(safeHTTPURL)
        return FetchResult(profiles: profiles, suggestedName: title ?? name,
                            trafficUsed: used, trafficTotal: total, expiresAt: expires,
                            skippedCount: skippedCount,
                            title: title, announce: announce, updateIntervalHours: interval,
                            webPageURL: web, supportURL: support)
    }

    /// Адрес из заголовка открываем только по http(s). Заголовок приходит с
    /// чужого сервера, а `NSWorkspace.open` выполнит и `file://`, и схему
    /// стороннего приложения — то есть недоверенный ввод получил бы право
    /// запускать что-то на машине человека.
    private static func safeHTTPURL(_ raw: String) -> URL? {
        let value = raw.trimmingCharacters(in: .whitespaces)
        guard let url = URL(string: value),
              let scheme = url.scheme?.lowercased(),
              scheme == "https" || scheme == "http",
              url.host != nil else { return nil }
        return url
    }

    /// Заголовок подписки приходит либо как `base64:...`, либо открытым
    /// текстом. Разбираем оба: часть панелей кодирует всегда, часть — только
    /// когда в строке не-ASCII, и угадывать по содержимому ненадёжно.
    private static func decodeHeaderText(_ raw: String) -> String? {
        let value = raw.trimmingCharacters(in: .whitespaces)
        guard !value.isEmpty else { return nil }
        let prefix = "base64:"
        guard value.lowercased().hasPrefix(prefix) else { return value }
        var payload = String(value.dropFirst(prefix.count))
        payload = payload.replacingOccurrences(of: "-", with: "+")
                         .replacingOccurrences(of: "_", with: "/")
        while payload.count % 4 != 0 { payload += "=" }
        guard let data = Data(base64Encoded: payload),
              let text = String(data: data, encoding: .utf8),
              !text.isEmpty else { return nil }
        return text
    }

    /// Некоторые панели выдают подписку по ссылке с плейсхолдером устройства,
    /// например `/sub/{hwid}` или `?device_id={device_id}`.
    private static func resolvedURL(_ url: URL) -> URL {
        let raw = url.absoluteString
        let replaced = raw
            .replacingOccurrences(of: "{hwid}", with: DeviceIdentity.hwid)
            .replacingOccurrences(of: "{HWID}", with: DeviceIdentity.hwid)
            .replacingOccurrences(of: "{device_id}", with: DeviceIdentity.hwid)
            .replacingOccurrences(of: "{DEVICE_ID}", with: DeviceIdentity.hwid)
            .replacingOccurrences(of: "{deviceId}", with: DeviceIdentity.hwid)
            .replacingOccurrences(of: "{DEVICEID}", with: DeviceIdentity.hwid)
        return URL(string: replaced) ?? url
    }

    // MARK: - Нативный sing-box JSON (формат нашего движка — самый надёжный путь)

    private static func singBoxOutbounds(_ data: Data) -> [[String: Any]]? {
        // Тело подписки приходит с чужого сервера, а `JSONSerialization`
        // разбирает документ рекурсией. В задаче, возобновлённой на потоке
        // кооперативного пула (а именно там мы и находимся — сразу после
        // `await URLSession`), глубоко вложенный JSON не даёт ошибку, а роняет
        // ВСЁ приложение по переполнению стека. Проверено на подписке из
        // тысячи вложенных объектов, см. `JSONDepthGuard`.
        guard JSONDepthGuard.isSafe(data) else { return nil }
        if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            if let outbounds = json["outbounds"] as? [[String: Any]] { return outbounds }
        }
        if let array = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] {
            let nested = array.compactMap { $0["outbounds"] as? [[String: Any]] }.flatMap { $0 }
            if !nested.isEmpty { return nested }
        }
        return nil
    }

    private static func vlessProfile(from ob: [String: Any]) -> ServerProfile? {
        guard let server = ob["server"] as? String,
              let port = ob["server_port"] as? Int,
              let uuid = ob["uuid"] as? String else { return nil }
        let tls = ob["tls"] as? [String: Any]
        let reality = tls?["reality"] as? [String: Any]
        guard let publicKey = reality?["public_key"] as? String, !publicKey.isEmpty else { return nil }
        let utls = tls?["utls"] as? [String: Any]
        let params = ServerProfile.Parameters.VLESSReality(
            uuid: uuid,
            flow: ob["flow"] as? String,
            sni: (tls?["server_name"] as? String) ?? server,
            publicKey: publicKey,
            shortID: (reality?["short_id"] as? String) ?? "",
            fingerprint: (utls?["fingerprint"] as? String) ?? "chrome"
        )
        return ServerProfile(name: (ob["tag"] as? String) ?? server, protocol: .vlessReality,
                              host: server, port: port, parameters: .vlessReality(params))
    }

    private static func hysteria2Profile(from ob: [String: Any]) -> ServerProfile? {
        guard let server = ob["server"] as? String,
              let port = ob["server_port"] as? Int,
              let password = ob["password"] as? String, !password.isEmpty else { return nil }
        let tls = ob["tls"] as? [String: Any]
        let obfs = ob["obfs"] as? [String: Any]
        let params = ServerProfile.Parameters.Hysteria2(
            password: password,
            sni: tls?["server_name"] as? String,
            allowInsecure: (tls?["insecure"] as? Bool) ?? false,
            obfsPassword: obfs?["password"] as? String,
            upMbps: ob["up_mbps"] as? Int,
            downMbps: ob["down_mbps"] as? Int,
            alpn: (tls?["alpn"] as? [String]) ?? ["h3"]
        )
        return ServerProfile(name: (ob["tag"] as? String) ?? server, protocol: .hysteria2,
                              host: server, port: port, parameters: .hysteria2(params))
    }

    /// Xray-подписка (используется, например, клиентом Happ) — верхнеуровневый массив
    /// объектов с ключами routing/inbounds. Пока не разбираем, только распознаём,
    /// чтобы показать понятную ошибку вместо «пусто».
    private static func looksLikeXrayJSON(_ data: Data) -> Bool {
        // Та же защита, что и в `singBoxOutbounds`: сюда приходит то же самое
        // чужое тело, и рекурсия разбора та же.
        guard JSONDepthGuard.isSafe(data) else { return false }
        guard let arr = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]],
              let first = arr.first else { return false }
        return first["routing"] != nil || first["inbounds"] != nil
    }

    /// Имя первой ссылки (из #fragment) — провайдеры кладут туда человекочитаемое
    /// сообщение, когда сервер фиктивный (host 0.0.0.0 и т.п.).
    private static func firstFragmentName(in text: String) -> String? {
        for line in text.split(whereSeparator: \.isNewline) {
            let s = line.trimmingCharacters(in: .whitespaces)
            guard !s.isEmpty, let fragment = URLComponents(string: s)?.fragment,
                  let decoded = fragment.removingPercentEncoding, !decoded.isEmpty else { continue }
            return decoded
        }
        return nil
    }

    private static func parseShareLinks(in text: String) -> (profiles: [ServerProfile], errors: [(line: Int, error: Error)]) {
        var links: [String] = []
        // Схемы перечислены полностью. Раньше здесь были только hysteria2 и
        // vless, и ссылки Trojan с Shadowsocks отсеивались ДО парсера — то
        // есть терялись молча, даже не попав в счётчик пропущенных. Из
        // одиннадцати ссылок подписки доезжало восемь, и понять, куда делись
        // три оси обхода, было неоткуда.
        if let regex = try? NSRegularExpression(pattern: #"(?:hysteria2|hy2|vless|trojan|ss)://[^\s"'<>]+"#,
                                                options: [.caseInsensitive]) {
            let range = NSRange(text.startIndex..., in: text)
            for match in regex.matches(in: text, range: range) {
                guard let r = Range(match.range, in: text) else { continue }
                links.append(String(text[r]))
            }
        }
        if links.isEmpty { return ShareLinkParser.parseMany(text) }
        return ShareLinkParser.parseMany(links.joined(separator: "\n"))
    }

    private static func parseClashYAML(_ text: String) -> (profiles: [ServerProfile], skippedCount: Int) {
        let proxies = clashProxyBlocks(text)
        var profiles: [ServerProfile] = []
        var skipped = 0
        for proxy in proxies {
            guard let type = proxy["type"]?.lowercased() else { continue }
            if type == "vless", let p = clashVLESS(proxy) {
                profiles.append(p)
            } else if (type == "hysteria2" || type == "hy2"), let p = clashHysteria2(proxy) {
                profiles.append(p)
            } else {
                skipped += 1
            }
        }
        return (profiles, skipped)
    }

    private static func clashProxyBlocks(_ text: String) -> [[String: String]] {
        var inProxies = false
        var current: [String: String]?
        var out: [[String: String]] = []

        for rawLine in text.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = String(rawLine)
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty, !trimmed.hasPrefix("#") else { continue }

            if !inProxies {
                if trimmed == "proxies:" { inProxies = true }
                continue
            }

            if !line.hasPrefix(" ") && !line.hasPrefix("-") {
                break
            }

            if trimmed.hasPrefix("- ") {
                if let current { out.append(current) }
                current = [:]
                parseClashLine(String(trimmed.dropFirst(2)), into: &current!)
            } else if current != nil {
                parseClashLine(trimmed, into: &current!)
            }
        }

        if let current { out.append(current) }
        return out
    }

    private static func parseClashLine(_ line: String, into dict: inout [String: String]) {
        guard let colon = line.firstIndex(of: ":") else { return }
        let key = String(line[..<colon]).trimmingCharacters(in: .whitespaces)
        var value = String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
        if value.hasPrefix("\""), value.hasSuffix("\""), value.count >= 2 {
            value = String(value.dropFirst().dropLast())
        } else if value.hasPrefix("'"), value.hasSuffix("'"), value.count >= 2 {
            value = String(value.dropFirst().dropLast())
        }
        if !key.isEmpty { dict[key] = value }
    }

    private static func clashVLESS(_ proxy: [String: String]) -> ServerProfile? {
        guard let server = proxy["server"], !server.isEmpty,
              let port = proxy["port"].flatMap(Int.init),
              let uuid = proxy["uuid"], !uuid.isEmpty else { return nil }
        let publicKey = proxy["public-key"] ?? proxy["public_key"] ?? proxy["reality-opts.public-key"]
        guard let publicKey, !publicKey.isEmpty else { return nil }
        let params = ServerProfile.Parameters.VLESSReality(
            uuid: uuid,
            flow: emptyToNil(proxy["flow"]),
            sni: proxy["servername"] ?? proxy["sni"] ?? server,
            publicKey: publicKey,
            shortID: proxy["short-id"] ?? proxy["short_id"] ?? "",
            fingerprint: proxy["client-fingerprint"] ?? proxy["fingerprint"] ?? "chrome"
        )
        return ServerProfile(
            name: proxy["name"] ?? server,
            protocol: .vlessReality,
            host: server,
            port: port,
            parameters: .vlessReality(params)
        )
    }

    private static func clashHysteria2(_ proxy: [String: String]) -> ServerProfile? {
        guard let server = proxy["server"], !server.isEmpty,
              let port = proxy["port"].flatMap(Int.init),
              let password = proxy["password"], !password.isEmpty else { return nil }
        let params = ServerProfile.Parameters.Hysteria2(
            password: password,
            sni: proxy["sni"] ?? proxy["servername"],
            allowInsecure: boolValue(proxy["skip-cert-verify"] ?? proxy["insecure"]),
            obfsPassword: emptyToNil(proxy["obfs-password"]),
            upMbps: proxy["up"].flatMap(Int.init),
            downMbps: proxy["down"].flatMap(Int.init),
            alpn: proxy["alpn"].map { $0.split(separator: ",").map { String($0).trimmingCharacters(in: .whitespaces) } } ?? ["h3"]
        )
        return ServerProfile(
            name: proxy["name"] ?? server,
            protocol: .hysteria2,
            host: server,
            port: port,
            parameters: .hysteria2(params)
        )
    }

    private static func boolValue(_ value: String?) -> Bool {
        guard let value = value?.lowercased() else { return false }
        return value == "true" || value == "1" || value == "yes"
    }

    private static func emptyToNil(_ value: String?) -> String? {
        guard let value, !value.isEmpty else { return nil }
        return value
    }

    /// Тело подписки: если уже содержит share-ссылки — используем как есть,
    /// иначе пробуем base64 (с URL-safe алфавитом и без паддинга).
    private static func decodeBody(_ data: Data) -> String {
        guard let raw = String(data: data, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines), !raw.isEmpty else { return "" }
        if raw.contains("://") { return raw }
        return base64Decoded(raw) ?? raw
    }

    private static func base64Decoded(_ s: String) -> String? {
        var padded = s.replacingOccurrences(of: "\n", with: "").replacingOccurrences(of: "\r", with: "")
        padded = padded.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while padded.count % 4 != 0 { padded += "=" }
        guard let data = Data(base64Encoded: padded), let text = String(data: data, encoding: .utf8) else { return nil }
        return text
    }

    private static func looksLikeYAML(_ text: String) -> Bool {
        text.hasPrefix("proxies:") || text.contains("\nproxies:") || text.contains("mixed-port:")
    }

    private static func htmlErrorMessage(_ data: Data) -> String? {
        guard let text = String(data: data, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines),
              text.localizedCaseInsensitiveContains("<html") else { return nil }
        if let title = firstMatch(in: text, pattern: #"<title>\s*([^<]+)\s*</title>"#) {
            return title.replacingOccurrences(of: "\n", with: " ")
        }
        if let h1 = firstMatch(in: text, pattern: #"<h1>\s*([^<]+)\s*</h1>"#) {
            return h1.replacingOccurrences(of: "\n", with: " ")
        }
        return "сервер подписки вернул HTML вместо конфигурации"
    }

    private static func firstMatch(in text: String, pattern: String) -> String? {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { return nil }
        let range = NSRange(text.startIndex..., in: text)
        guard let match = regex.firstMatch(in: text, range: range),
              match.numberOfRanges > 1,
              let r = Range(match.range(at: 1), in: text) else { return nil }
        return String(text[r]).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func extractFilename(_ disposition: String) -> String? {
        guard let range = disposition.range(of: "filename=") else { return nil }
        let name = String(disposition[range.upperBound...])
            .trimmingCharacters(in: CharacterSet(charactersIn: "\" "))
        return name.isEmpty ? nil : (name.removingPercentEncoding ?? name)
    }

    /// "upload=123; download=456; total=789; expire=1712345678"
    private static func parseUserinfo(_ s: String) -> (Int64?, Int64?, Date?) {
        var dict: [String: Int64] = [:]
        for part in s.split(separator: ";") {
            let kv = part.split(separator: "=", maxSplits: 1)
            guard kv.count == 2, let v = Int64(kv[1].trimmingCharacters(in: .whitespaces)) else { continue }
            dict[kv[0].trimmingCharacters(in: .whitespaces)] = v
        }
        let hasTraffic = dict["upload"] != nil || dict["download"] != nil
        let used = hasTraffic ? (dict["upload"] ?? 0) + (dict["download"] ?? 0) : nil
        let expires = dict["expire"].map { Date(timeIntervalSince1970: TimeInterval($0)) }
        return (used, dict["total"], expires)
    }
}
