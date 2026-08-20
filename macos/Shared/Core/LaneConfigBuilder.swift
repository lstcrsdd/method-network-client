import Foundation

/// Сборка конфига с ПОЛОСАМИ вместо одного исходящего соединения.
///
/// Зачем это отдельно от `SingBoxConfigBuilder`. Тот собирает конфиг с одним
/// outbound на весь туннель, и потому смена сервера у него — это остановка
/// ядра и подъём заново: рвутся все соединения, пропадает интернет на
/// несколько секунд. Пока так, адаптивный выбор маршрута невозможен в
/// принципе, а не «не реализован».
///
/// Здесь конфиг описывает не сервер, а структуру:
///
///   маршрут (R.*)   — конкретный узел с конкретным транспортом;
///   ось    (AX.*)   — независимый способ пройти, группа маршрутов;
///   полоса (L.*)    — класс трафика, селектор поверх осей.
///
/// Правила маршрутизации называют ПОЛОСУ и никогда не называют маршрут.
/// Какой маршрут стоит за полосой сейчас — решает управляющий слой одним
/// запросом к локальному API ядра, не трогая конфиг и не пересоздавая
/// туннель.
///
/// Проверено на живом ядре 1.13.13 и боевых узлах 2026-08-20:
///   * одиннадцать маршрутов свернулись в пять осей, конфиг принят;
///   * `PUT /proxies/L.web {"name":"AX.fake-tls-h2"}` → 204, выходной адрес
///     сменился с американского на финский без пересборки конфига;
///   * `PUT` на существующий, но НЕ входящий в селектор `OUT_direct` →
///     `400 Selector update error: not found`, полоса осталась на месте.
///
/// Последнее — главное свойство схемы. «Этот трафик никогда не пойдёт
/// открытым» держит САМО ЯДРО, а не наша аккуратность: множество состояний,
/// достижимых через API, конечно и целиком выводится из статического файла.
/// Даже локальный процесс, завладевший секретом API, не выведет полосу за
/// пределы объявленного набора.
public enum LaneConfigBuilder {

    // MARK: - Имена

    public enum Tag {
        /// Нулевой outbound ВСЕГДА блокирующий. У ядра поведение по умолчанию
        /// fail-open: конфиг без `final` отправляет трафик в первый outbound
        /// по порядку массива, а конфиг вовсе без секции outbounds стартует с
        /// неявным прямым выходом. Нейтрализуем это осознанно, чтобы любая
        /// мыслимая деградация конфига давала тишину, а не открытый выход.
        public static let block = "OUT_block"
        public static let direct = "OUT_direct"
        public static let remoteDNS = "remote"
        public static let bootstrapDNS = "bootstrap"
        public static let defaultLane = "L.web"

        public static func route(_ index: Int) -> String { "R\(index)" }
        public static func lane(_ id: String) -> String { "L.\(id)" }
        public static func axis(_ axis: EvasionAxisKey) -> String { "AX.\(axis.rawValue)" }
    }

    /// Ось обхода. Дублирует перечисление ядра оркестратора намеренно: общий
    /// слой не должен зависеть от Rust-библиотеки, иначе его нельзя собрать
    /// без неё.
    public enum EvasionAxisKey: String, CaseIterable {
        case quicUDP = "quic-udp"
        case fakeTLSH2 = "fake-tls-h2"
        case fakeTLSTCP = "fake-tls-tcp"
        case realTLS = "real-tls"
        case rawStream = "raw-stream"

        /// Как ось называется в разговоре с человеком.
        ///
        /// Имя живёт здесь, а не в интерфейсе, потому что его вставляет в свои
        /// объяснения движок: он получает ось под этим именем и пишет
        /// «ушли с QUIC поверх UDP», а не «ушли с AX.quic-udp». Технический
        /// тег человеку не говорит ничего.
        public var human: String {
            switch self {
            case .quicUDP:    return "QUIC поверх UDP"
            case .fakeTLSH2:  return "gRPC в поддельном TLS"
            case .fakeTLSTCP: return "TCP в поддельном TLS"
            case .realTLS:    return "настоящий TLS"
            case .rawStream:  return "поток без рукопожатия"
            }
        }

        public static func of(_ profile: ServerProfile) -> EvasionAxisKey {
            switch profile.parameters {
            case .hysteria2:
                return .quicUDP
            case .vlessReality(let v):
                // gRPC и Vision — разные оси: их ломает разное, хотя протокол
                // один и тот же.
                return (v.grpcServiceName?.isEmpty == false) ? .fakeTLSH2 : .fakeTLSTCP
            case .trojan:
                return .realTLS
            case .shadowsocks:
                return .rawStream
            }
        }
    }

    // MARK: - Результат сборки

    /// То, что управляющему слою нужно знать о собранном конфиге, чтобы им
    /// управлять. Без этого он был бы вынужден разбирать конфиг обратно.
    public struct Plan {
        /// Тег полосы -> теги её членов (оси и блокировка).
        public let lanes: [String: [String]]
        /// Тег полосы, через которую идёт остальной трафик. Нужен тем, кто
        /// показывает человеку «а где я сейчас»: спрашивать надо именно её.
        public let defaultLaneTag: String
        /// Тег полосы -> человеческое имя. Движок вставляет его в объяснения
        /// дословно, поэтому «Весь трафик» здесь важнее, чем «L.web».
        public let laneTitles: [String: String]
        /// Тег оси -> теги маршрутов внутри неё.
        public let axes: [String: [String]]
        /// Тег маршрута -> профиль, который за ним стоит.
        public let routes: [String: ServerProfile]
        /// Адреса всех узлов — для пакетного фильтра: он принимает только
        /// литералы, а у Trojan адрес доменный и его надо разрешить заранее.
        public let hosts: [String]
        public let config: [String: Any]
    }

    // MARK: - Сборка

    /// Сборка по политике: несколько полос и правила «что куда».
    ///
    /// Бросает, если политику собирать нельзя. Отказ здесь дешевле, чем
    /// авария у пользователя: правило без полосы, полоса с прямым выходом
    /// без объяснения, остаток трафика в открытую полосу — всё это ловится
    /// до того, как ядро увидит конфиг.
    public static func build(
        profiles: [ServerProfile],
        policy: LanePolicy,
        options: SingBoxConfigBuilder.Options
    ) throws -> Plan {
        let axesPresent = Set(profiles.map { EvasionAxisKey.of($0) })
        try policy.validate(availableAxes: axesPresent)
        return assemble(profiles: profiles, policy: policy, options: options)
    }

    public static func build(
        profiles: [ServerProfile],
        options: SingBoxConfigBuilder.Options
    ) -> Plan {
        assemble(profiles: profiles, policy: nil, options: options)
    }

    private static func assemble(
        profiles: [ServerProfile],
        policy: LanePolicy?,
        options: SingBoxConfigBuilder.Options
    ) -> Plan {
        var routeOutbounds: [[String: Any]] = []
        var routeMap: [String: ServerProfile] = [:]
        var byAxis: [EvasionAxisKey: [String]] = [:]
        var hosts: [String] = []

        for (i, profile) in profiles.enumerated() {
            let tag = Tag.route(i)
            var o = SingBoxConfigBuilder.outboundJSON(for: profile)
            o["tag"] = tag
            // Адрес самой ноды разрешается МИМО туннеля. Иначе замыкание:
            // чтобы поднять туннель, надо узнать адрес узла (у Trojan это
            // домен), а чтобы узнать адрес — нужен поднятый туннель.
            o["domain_resolver"] = ["server": Tag.bootstrapDNS]
            routeOutbounds.append(o)
            routeMap[tag] = profile
            byAxis[EvasionAxisKey.of(profile), default: []].append(tag)
            if !hosts.contains(profile.host) { hosts.append(profile.host) }
        }

        // Ось — группа urltest. Нижний ярус автономии: даже если управляющий
        // слой умрёт, ядро само уйдёт с мёртвого маршрута внутри оси.
        // Переоценивать это нельзя — пробы там ленивые и история в одну
        // запись, — но как страховка это лучше, чем ничего.
        var axisGroups: [[String: Any]] = []
        var axisMap: [String: [String]] = [:]
        for axis in EvasionAxisKey.allCases {
            guard let members = byAxis[axis], !members.isEmpty else { continue }
            let tag = Tag.axis(axis)
            axisMap[tag] = members
            axisGroups.append([
                "type": "urltest",
                "tag": tag,
                "outbounds": members,
                "url": options.probeURL,
                "interval": "3m",
                // Порог улучшения у ядра грубый и по одному замеру. Нам он
                // нужен только чтобы ядро не металось само; настоящий
                // гистерезис живёт в управляющем слое.
                "tolerance": 100,
            ])
        }

        // Полоса — селектор. Состав селектора и есть множество состояний,
        // достижимых через API: что не перечислено здесь, недостижимо.
        let specs = policy?.lanes ?? [LanePolicy.Lane(id: "web", title: "Весь трафик")]
        var lanes: [String: [String]] = [:]
        var laneTitles: [String: String] = [:]
        var laneSelectors: [[String: Any]] = []

        for spec in specs {
            let tag = Tag.lane(spec.id)
            var members: [String]
            if spec.allowsDirect {
                // Полоса прямого выхода не содержит осей вовсе: у неё один
                // допустимый исход, и подменить его через API нельзя.
                members = [Tag.direct]
            } else {
                let allowed = spec.axisIn.map { Set($0) }
                members = axisMap.keys.sorted().filter { tag in
                    guard let allowed else { return true }
                    return allowed.contains { Tag.axis($0) == tag }
                }
                members.append(Tag.block)
            }
            lanes[tag] = members
            laneTitles[tag] = spec.title
            laneSelectors.append([
                "type": "selector",
                "tag": tag,
                "outbounds": members,
                "default": members.first ?? Tag.block,
                // Смена маршрута НЕ рвёт живые соединения. Это осознанный
                // выбор: при плановом переключении сессии дотекают по
                // прежнему пути, а рвать их надо только при аварии или
                // ужесточении политики — отдельным запросом.
                "interrupt_exist_connections": false,
            ])
        }

        var outbounds: [[String: Any]] = [
            ["type": "block", "tag": Tag.block],
            // Прямому выходу — СВОЙ резолвер, мимо туннеля. Две причины, и
            // обе выяснились на живом прогоне. Первая: общий резолвер уведён
            // внутрь туннеля, и разрешение имени для прямого соединения через
            // него просто не работает — ядро отвечает «read/write on closed
            // pipe». Вторая важнее: даже когда работает, имя, разрешённое
            // через выходной узел, возвращает адрес чужого узла CDN, и
            // российский сервис, ради которого правило и написано, отвечает
            // медленно или отказывает. Резолв прямого трафика обязан идти
            // тем же путём, что сам трафик.
            ["type": "direct", "tag": Tag.direct,
             "domain_resolver": ["server": Tag.bootstrapDNS]],
        ]
        outbounds.append(contentsOf: routeOutbounds)
        outbounds.append(contentsOf: axisGroups)
        outbounds.append(contentsOf: laneSelectors)

        let config: [String: Any] = [
            "log": ["level": options.logLevel, "timestamp": true],
            "dns": dns(),
            "inbounds": [SingBoxConfigBuilder.tunInboundJSON(mtu: options.mtu)],
            "outbounds": outbounds,
            "route": route(hosts: hosts, policy: policy),
            "experimental": [
                "clash_api": [
                    "external_controller": options.clashController,
                    "secret": options.clashSecret,
                ],
            ],
        ]

        let defaultTag = LaneConfigBuilder.Tag.lane(policy?.defaultLane ?? "web")
        return Plan(lanes: lanes, defaultLaneTag: defaultTag, laneTitles: laneTitles,
                    axes: axisMap, routes: routeMap, hosts: hosts, config: config)
    }

    // MARK: - Секции

    private static func dns() -> [String: Any] {
        [
            "servers": [
                // Резолвер внутри туннеля — через ПОЛОСУ, а не через
                // конкретный маршрут: иначе при смене маршрута DNS остался бы
                // на старом.
                ["tag": Tag.remoteDNS, "type": "udp", "server": "1.1.1.1",
                 "detour": Tag.defaultLane],
                // Единственный резолвер мимо туннеля, и нужен ровно для
                // одного: узнать адрес узла до того, как туннель встал.
                // Ссылается на него только `domain_resolver` у маршрутов —
                // ни одно правило и ни один `final` сюда не ведут.
                ["tag": Tag.bootstrapDNS, "type": "udp", "server": "77.88.8.8"],
            ],
            "final": Tag.remoteDNS,
            "strategy": "ipv4_only",
        ]
    }

    private static func route(hosts: [String], policy: LanePolicy?) -> [String: Any] {
        var rules: [[String: Any]] = [
            ["action": "sniff"],
            ["protocol": "dns", "action": "hijack-dns"],
        ]
        // Трафик к самим узлам обязан идти напрямую, иначе туннель замкнётся
        // сам на себя.
        let literals = hosts.filter(isIPv4Address)
        if !literals.isEmpty {
            rules.append(["ip_cidr": literals.map { "\($0)/32" }, "outbound": Tag.direct])
        }
        let domains = hosts.filter { !isIPv4Address($0) }
        if !domains.isEmpty {
            rules.append(["domain": domains, "outbound": Tag.direct])
        }
        // Правила политики — в порядке написания, первое совпадение
        // выигрывает. Порядок ядра мы не переупорядочиваем: любая наша
        // математика приоритетов создала бы расхождение между тем, что
        // человек читает, и тем, что происходит.
        if let policy {
            for flow in policy.flows {
                var rule = matchJSON(flow.match)
                rule["outbound"] = Tag.lane(flow.lane)
                rules.append(rule)
            }
        } else {
            rules.append(["ip_is_private": true, "outbound": Tag.direct])
        }

        let finalLane = policy.map { Tag.lane($0.defaultLane) } ?? Tag.defaultLane
        return [
            "default_domain_resolver": ["server": Tag.remoteDNS],
            "rules": rules,
            // Второй эшелон тотальности: остаток всегда в полосе, а полоса
            // остатка по проверке политики не может выпускать открытым.
            "final": finalLane,
            "auto_detect_interface": true,
        ]
    }

    private static func matchJSON(_ match: LanePolicy.Match) -> [String: Any] {
        switch match {
        case .domainSuffix(let d):  return ["domain_suffix": d]
        case .domainKeyword(let d): return ["domain_keyword": d]
        case .processName(let p):   return ["process_name": p]
        case .port(let p):          return ["port": p]
        case .ipIsPrivate:          return ["ip_is_private": true]
        }
    }

    private static func isIPv4Address(_ host: String) -> Bool {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        return parts.allSatisfy { part in
            guard let value = Int(part), part.count <= 3, !part.isEmpty else { return false }
            return (0...255).contains(value)
        }
    }
}
