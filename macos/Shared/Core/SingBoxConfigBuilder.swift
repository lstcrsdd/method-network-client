import Foundation

/// Собирает sing-box JSON-конфиг из `ServerProfile` для режима TUN.
///
/// Схема выверена против sing-box **1.13.x** (`sing-box check`):
///   - `route.default_domain_resolver` обязателен (миграция 1.12);
///   - новый формат DNS-серверов (`type`/`server`);
///   - sniffing и DNS-hijack — через route-actions, а не поля inbound.
public enum SingBoxConfigBuilder {

    public struct Options: Sendable {
        /// Адрес Clash API (управление + статистика трафика).
        public var clashController: String
        /// Секрет Clash API (Bearer-токен).
        public var clashSecret: String
        /// Уровень логирования sing-box.
        public var logLevel: String
        /// MTU для utun. 1380, а не 1400: наружу пакет уходит внутри QUIC-датаграммы
        /// вместе с заголовками Hysteria2/QUIC/UDP/IP, и при 1400 итог не пролезал
        /// в путь мобильного оператора — соединение флапало, DNS не укладывался
        /// в таймаут. 1380 оставляет ~70 байт запаса.
        public var mtu: Int
        /// Адрес проверки для групп по осям. ТОЛЬКО https: ядро игнорирует
        /// http-адрес и молча подставляет собственный на gstatic — измерение
        /// уехало бы на Google, а этот хост в РФ чувствителен.
        public var probeURL: String

        public init(
            clashController: String = "127.0.0.1:9090",
            clashSecret: String,
            logLevel: String = "info",
            mtu: Int = 1380,
            probeURL: String = "https://www.gstatic.com/generate_204"
        ) {
            self.clashController = clashController
            self.clashSecret = clashSecret
            self.logLevel = logLevel
            self.mtu = mtu
            self.probeURL = probeURL
        }
    }

    /// Сериализованный JSON-конфиг (UTF-8), готовый для `sing-box run -c`.
    public static func buildJSON(for profile: ServerProfile, options: Options) throws -> Data {
        let config = build(for: profile, options: options)
        return try JSONSerialization.data(
            withJSONObject: config,
            options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
    }

    public static func buildJSONString(for profile: ServerProfile, options: Options) throws -> String {
        let data = try buildJSON(for: profile, options: options)
        guard let str = String(data: data, encoding: .utf8) else {
            throw NSError(domain: "SingBoxConfigBuilder", code: 1, userInfo: [
                NSLocalizedDescriptionKey: "Не удалось сериализовать конфиг в UTF-8",
            ])
        }
        return str
    }

    /// Локальный SOCKS/mixed-конфиг для проверки протокола до поднятия системного TUN.
    /// Используется только кратковременным probe-процессом приложения.
    public static func buildProbeJSONString(
        for profile: ServerProfile,
        listenPort: Int,
        bindInterface: String?
    ) throws -> String {
        var proxy = outbound(for: profile)
        if let bindInterface, !bindInterface.isEmpty {
            proxy["bind_interface"] = bindInterface
        }

        let config: [String: Any] = [
            "log": ["level": "error", "timestamp": true],
            "inbounds": [[
                "type": "mixed",
                "tag": "probe-in",
                "listen": "127.0.0.1",
                "listen_port": listenPort,
            ]],
            "outbounds": [proxy],
            "route": ["final": Tag.proxy],
        ]
        let data = try JSONSerialization.data(
            withJSONObject: config,
            options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        )
        guard let string = String(data: data, encoding: .utf8) else {
            throw NSError(domain: "SingBoxConfigBuilder", code: 2, userInfo: [
                NSLocalizedDescriptionKey: "Не удалось сериализовать probe-конфиг",
            ])
        }
        return string
    }

    public static let defaultOptions = Options(clashSecret: "method-vpn-clash-secret")

    /// Конфиг в виде дерева словарей (для тестов и сериализации).
    public static func build(for profile: ServerProfile, options: Options) -> [String: Any] {
        // Адрес самой ноды разрешается ОТДЕЛЬНЫМ резолвером, мимо туннеля.
        // Иначе получается замыкание: чтобы поднять туннель, надо узнать адрес
        // ноды (у Trojan это домен, а не IP), а чтобы узнать адрес — нужен уже
        // поднятый туннель. См. `dns()` и `Tag.bootstrapDNS`.
        var proxy = outbound(for: profile)
        proxy["domain_resolver"] = ["server": Tag.bootstrapDNS]

        return [
            "log": ["level": options.logLevel, "timestamp": true],
            "dns": dns(),
            "inbounds": [tunInbound(mtu: options.mtu)],
            "outbounds": [proxy, ["type": "direct", "tag": Tag.direct]],
            "route": route(for: profile),
            "experimental": [
                "clash_api": [
                    "external_controller": options.clashController,
                    "secret": options.clashSecret,
                ]
            ],
        ]
    }

    // MARK: - Теги

    private enum Tag {
        static let proxy = "proxy"
        static let direct = "direct"
        /// Резолвер внутри туннеля — всё, что ядро разрешает само.
        static let remoteDNS = "remote"
        /// Резолвер мимо туннеля — ТОЛЬКО адрес самой ноды.
        static let bootstrapDNS = "bootstrap"
    }

    // MARK: - Секции

    private static func dns() -> [String: Any] {
        [
            "servers": [
                // Обычный UDP внутрь туннеля, а не DNS-over-TLS. DoT держит
                // долгоживущий TCP-сеанс, и внутри QUIC он залипал: в логах
                // «outbound connection to 1.1.1.1:853» висел по 16–43 секунды
                // и валился в «context deadline exceeded», из-за чего вставал
                // резолв и весь VPN выглядел нерабочим при живом туннеле.
                // Приватность не страдает — запрос идёт внутри шифрованного канала.
                ["tag": Tag.remoteDNS, "type": "udp", "server": "1.1.1.1", "detour": Tag.proxy],
                // Единственный резолвер, ходящий мимо туннеля, и он нужен ровно
                // для одного: узнать адрес самой ноды до того, как туннель встал.
                // Ссылается на него ТОЛЬКО `domain_resolver` у proxy-outbound —
                // ни `final`, ни какое-либо правило сюда не ведут, поэтому имена,
                // которые открывает пользователь, сюда попасть не могут.
                //
                // Яндекс, а не 223.5.5.5: тот — AliDNS в Китае, из России до него
                // далеко и медленно. `detour` здесь ставить нельзя — ядро отвечает
                // FATAL «detour to an empty direct outbound makes no sense»
                // (проверено на 1.13.13); отсутствие detour и означает «напрямую».
                ["tag": Tag.bootstrapDNS, "type": "udp", "server": "77.88.8.8"],
            ],
            // Правила clash_mode убраны намеренно. Ими никто не пользовался
            // (режим по умолчанию — Rule, ни один клиент его не переключает),
            // а правило «Direct → нетуннелированный резолвер» было рычагом
            // утечки: маршрут трафика от режима Clash здесь не зависит, то есть
            // переключение дало бы DNS мимо туннеля при трафике внутри туннеля.
            "final": Tag.remoteDNS,
            "strategy": "ipv4_only",
        ]
    }

    private static func tunInbound(mtu: Int) -> [String: Any] {
        [
            "type": "tun",
            "tag": "tun-in",
            // 198.18.0.0/15 — диапазон RFC 2544, отведённый под тестирование
            // оборудования. Реальные сети им не пользуются, поэтому его берут
            // Clash, Happ и прочие клиенты.
            //
            // Было 172.18.0.1/30, и это ломало клиент в корпоративных сетях:
            // офисный роутер раздавал маршрут на весь 172.16/12 (а заодно на
            // 100.64/10 и 192.168.0/16), наш адрес оказывался внутри чужого
            // маршрута, и туннель не вставал. Снаружи всё работало, поэтому
            // выглядело как «сервер не отвечает».
            "address": ["198.18.2.1/30"],
            "mtu": mtu,
            "auto_route": true,
            "strict_route": true,
            "stack": "system",
        ]
    }

    private static func route(for profile: ServerProfile) -> [String: Any] {
        [
            // Инвариант I11. Всё, что ядро разрешает САМО (адреса для direct-outbound,
            // будущие правила с `action: resolve`), уходит внутрь туннеля.
            // Было `local` — резолвер 77.88.8.8 без detour: при полностью поднятом
            // туннеле имена уходили открытым UDP по физическому интерфейсу.
            // Проверено запуском на 1.13.13: соединение, отправленное правилом в
            // `direct` по доменному имени, резолвится именно этим резолвером.
            //
            // Замыкания не возникает: адрес самой ноды разрешает `domain_resolver`
            // у proxy-outbound (см. `build`), а не этот резолвер.
            //
            // Следствие, о котором надо знать: соединение, отправленное правилом
            // в `direct` по ДОМЕННОМУ имени (сегодня такое правило ровно одно —
            // адрес самой ноды), теперь резолвится через туннель. Выбор в пользу
            // отказа-закрытия: при мёртвом туннеле имя не разрешится вовсе, зато
            // мимо туннеля не уйдёт ничего. Если когда-нибудь появится правило
            // «РФ напрямую» с доменами, ему понадобится СВОЙ `domain_resolver` —
            // иначе список российских сайтов поедет внутрь туннеля, а трафик мимо.
            "default_domain_resolver": ["server": Tag.remoteDNS],
            "rules": [
                ["action": "sniff"],
                ["protocol": "dns", "action": "hijack-dns"],
                serverDirectRule(host: profile.host),
                ["ip_is_private": true, "outbound": Tag.direct],
            ],
            "final": Tag.proxy,
            "auto_detect_interface": true,
        ]
    }

    private static func serverDirectRule(host: String) -> [String: Any] {
        if host.contains(":") {
            return ["ip_cidr": ["\(host)/128"], "outbound": Tag.direct]
        }
        if isIPv4Address(host) {
            return ["ip_cidr": ["\(host)/32"], "outbound": Tag.direct]
        }
        return ["domain": [host], "outbound": Tag.direct]
    }

    private static func isIPv4Address(_ host: String) -> Bool {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        return parts.allSatisfy { part in
            guard let octet = Int(part) else { return false }
            return (0...255).contains(octet)
        }
    }

    // MARK: - Outbound по протоколу

    /// Открыто для `LaneConfigBuilder`: outbound одного маршрута одинаков в
    /// обеих схемах, и дублировать его значило бы разъехаться при первой же
    /// правке транспорта.
    public static func outboundJSON(for profile: ServerProfile) -> [String: Any] {
        outbound(for: profile)
    }

    /// Открыто для `LaneConfigBuilder` по той же причине: параметры TUN
    /// выстраданы (MTU 1380, диапазон RFC 2544) и должны быть одни на всех.
    public static func tunInboundJSON(mtu: Int) -> [String: Any] {
        tunInbound(mtu: mtu)
    }

    private static func outbound(for profile: ServerProfile) -> [String: Any] {
        switch profile.parameters {
        case .hysteria2(let p):    return hysteria2Outbound(profile, p)
        case .vlessReality(let p): return vlessOutbound(profile, p)
        case .trojan(let p):       return trojanOutbound(profile, p)
        case .shadowsocks(let p):  return shadowsocksOutbound(profile, p)
        }
    }

    private static func hysteria2Outbound(
        _ profile: ServerProfile, _ p: ServerProfile.Parameters.Hysteria2
    ) -> [String: Any] {
        var out: [String: Any] = [
            "type": "hysteria2",
            "tag": Tag.proxy,
            "server": profile.host,
            "server_port": profile.port,
            "password": p.password,
            "tls": [
                "enabled": true,
                "server_name": p.sni ?? profile.host,
                "insecure": p.allowInsecure,
                "alpn": p.alpn,
            ] as [String: Any],
        ]
        if let up = p.upMbps { out["up_mbps"] = up }
        if let down = p.downMbps { out["down_mbps"] = down }
        if let obfs = p.obfsPassword {
            out["obfs"] = ["type": "salamander", "password": obfs]
        }
        // Прыжки по портам намеренно НЕ включаются, хотя поля для них остались
        // в модели ради старых каталогов. Замер 2026-07-30, по 4 запроса на
        // каждый из шести инстансов: с прыжками 0–2 из 4, на фиксированном
        // порту 4 из 4 везде. За NAT смена порта раз в 30 с плодит записи
        // трансляции быстрее, чем роутер успевает их удерживать, и часть
        // сессий отваливается. За домашним роутером и за NAT сотового
        // оператора воспроизводится одинаково — то есть почти у всех.
        return out
    }

    private static func vlessOutbound(
        _ profile: ServerProfile, _ p: ServerProfile.Parameters.VLESSReality
    ) -> [String: Any] {
        var out: [String: Any] = [
            "type": "vless",
            "tag": Tag.proxy,
            "server": profile.host,
            "server_port": profile.port,
            "uuid": p.uuid,
            "packet_encoding": "xudp",
            "tls": [
                "enabled": true,
                "server_name": p.sni,
                "utls": ["enabled": true, "fingerprint": p.fingerprint],
                "reality": [
                    "enabled": true,
                    "public_key": p.publicKey,
                    "short_id": p.shortID,
                ],
            ],
        ]
        if let service = p.grpcServiceName, !service.isEmpty {
            // gRPC несовместим с flow: Vision работает только поверх голого TCP,
            // и вместе они дают отказ аутентификации на стороне Xray — соединение
            // принимается, но данные не идут. Поэтому flow здесь не выставляем.
            out["transport"] = ["type": "grpc", "service_name": service]
        } else if let flow = p.flow, !flow.isEmpty {
            out["flow"] = flow
        }
        return out
    }

    /// Trojan поверх настоящего TLS.
    ///
    /// В отличие от Reality тут ничего не подделывается: сервер предъявляет
    /// подлинный сертификат, поэтому `server_name` обязан совпадать с именем
    /// в нём. Клиент без верного пароля сервер отправляет на запасной сайт —
    /// снаружи это неотличимо от обычного HTTPS.
    private static func trojanOutbound(
        _ profile: ServerProfile, _ p: ServerProfile.Parameters.Trojan
    ) -> [String: Any] {
        var tls: [String: Any] = [
            "enabled": true,
            "server_name": p.sni ?? profile.host,
            "insecure": p.allowInsecure,
        ]
        if let fp = p.fingerprint, !fp.isEmpty {
            tls["utls"] = ["enabled": true, "fingerprint": fp]
        }
        var out: [String: Any] = [
            "type": "trojan",
            "tag": Tag.proxy,
            "server": profile.host,
            "server_port": profile.port,
            "password": p.password,
            "tls": tls,
        ]
        if let service = p.grpcServiceName, !service.isEmpty {
            out["transport"] = ["type": "grpc", "service_name": service]
        } else if let path = p.wsPath, !path.isEmpty {
            var ws: [String: Any] = ["type": "ws", "path": path]
            if let host = p.wsHost, !host.isEmpty {
                ws["headers"] = ["Host": host]
            }
            out["transport"] = ws
        }
        return out
    }

    /// Shadowsocks.
    ///
    /// Ни TLS, ни рукопожатия — просто шифрованный поток. Поэтому у него
    /// форма трафика, непохожая ни на Reality, ни на QUIC: полезен как ещё
    /// один независимый путь, когда остальные распознают.
    ///
    /// Имя шифра передаём ядру как есть. Список поддерживаемых у sing-box
    /// свой, и подменять его догадками нельзя: у семейства 2022 пароль — это
    /// base64-ключ строго определённой длины, привязанной к шифру.
    private static func shadowsocksOutbound(
        _ profile: ServerProfile, _ p: ServerProfile.Parameters.Shadowsocks
    ) -> [String: Any] {
        [
            "type": "shadowsocks",
            "tag": Tag.proxy,
            "server": profile.host,
            "server_port": profile.port,
            "method": p.method,
            "password": p.password,
        ]
    }
}
