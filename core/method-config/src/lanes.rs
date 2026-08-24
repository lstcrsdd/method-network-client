//! Сборка конфига в схеме ПОЛОС — двухъярусная автономия.
//!
//! ```text
//!   полоса (селектор) → ось (urltest) → маршрут
//! ```
//!
//! Внутри оси живого держит само ядро: у него для этого группа с собственными
//! пробами, и она работает, даже если управляющий контур умрёт. Верхний ярус —
//! выбор ОСИ — принадлежит движку: три сервера на одной оси убивает одна и та
//! же причина, и перебор между ними при блокировке не даёт ничего.
//!
//! Правила данных называют ПОЛОСУ и никогда — маршрут. Что не перечислено в
//! составе селектора, недостижимо через API: увод трафика за пределы
//! объявленного набора ядро отвергает само.

use crate::policy::{describe, LanePolicy, ValidationError};
use crate::profile::{Parameters, ServerProfile};
use crate::singbox::{self, tag};
use serde_json::{json, Value};
use std::collections::{BTreeMap, BTreeSet};

/// Ось обхода — независимый СПОСОБ пройти.
///
/// Считать надо не протоколы, а способы: протоколы на одной оси убивает одно и
/// то же. Hysteria2 и TUIC оба QUIC — сеть, режущая QUIC, забирает сразу оба.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum EvasionAxis {
    /// QUIC поверх UDP: Hysteria2.
    QuicUdp,
    /// HTTP/2 в поддельном TLS: Reality gRPC.
    FakeTlsH2,
    /// Голый TCP в поддельном TLS: Reality Vision.
    FakeTlsTcp,
    /// Настоящий TLS: Trojan. Единственный, кто ничего не подделывает.
    RealTls,
    /// Шифрованный поток без рукопожатия: Shadowsocks-2022.
    RawStream,
}

impl EvasionAxis {
    pub const ALL: [EvasionAxis; 5] = [
        EvasionAxis::QuicUdp,
        EvasionAxis::FakeTlsH2,
        EvasionAxis::FakeTlsTcp,
        EvasionAxis::RealTls,
        EvasionAxis::RawStream,
    ];

    pub fn key(self) -> &'static str {
        match self {
            EvasionAxis::QuicUdp => "quic-udp",
            EvasionAxis::FakeTlsH2 => "fake-tls-h2",
            EvasionAxis::FakeTlsTcp => "fake-tls-tcp",
            EvasionAxis::RealTls => "real-tls",
            EvasionAxis::RawStream => "raw-stream",
        }
    }

    pub fn human(self) -> &'static str {
        match self {
            EvasionAxis::QuicUdp => "QUIC поверх UDP",
            EvasionAxis::FakeTlsH2 => "gRPC в поддельном TLS",
            EvasionAxis::FakeTlsTcp => "TCP в поддельном TLS",
            EvasionAxis::RealTls => "настоящий TLS",
            EvasionAxis::RawStream => "поток без рукопожатия",
        }
    }

    /// Ось профиля. gRPC и Vision — РАЗНЫЕ оси, хотя протокол один: их ломает
    /// разное, и считать их одним способом значит обманывать себя.
    pub fn of(profile: &ServerProfile) -> EvasionAxis {
        match &profile.parameters {
            Parameters::Hysteria2 { .. } => EvasionAxis::QuicUdp,
            Parameters::VlessReality { params } => {
                match params.grpc_service_name.as_deref() {
                    Some(s) if !s.is_empty() => EvasionAxis::FakeTlsH2,
                    _ => EvasionAxis::FakeTlsTcp,
                }
            }
            Parameters::Trojan { .. } => EvasionAxis::RealTls,
            Parameters::Shadowsocks { .. } => EvasionAxis::RawStream,
        }
    }
}

pub fn route_tag(index: usize) -> String {
    format!("R{index}")
}
pub fn lane_tag(id: &str) -> String {
    format!("L.{id}")
}
pub fn axis_tag(axis: EvasionAxis) -> String {
    format!("AX.{}", axis.key())
}
pub const DEFAULT_LANE_TAG: &str = "L.web";

/// Блокировка. ОБЯЗАНА быть первым outbound: поведение ядра по умолчанию —
/// выпустить наружу, и без явной блокировки «живых маршрутов не осталось»
/// означало бы открытый трафик.
pub const BLOCK: &str = "OUT_block";

/// Прямой выход в схеме полос. Тег СВОЙ, не такой, как в одиночной схеме:
/// там он «direct», здесь «OUT_direct». Спутать их — значит собрать полосу,
/// которая ссылается на несуществующий outbound, и ядро молча отдаст трафик
/// в `final`.
pub const DIRECT: &str = "OUT_direct";

/// То, что управляющему слою нужно знать о собранном конфиге, чтобы им
/// управлять. Без этого он был бы вынужден разбирать конфиг обратно.
#[derive(Debug, Clone)]
pub struct Plan {
    /// Тег полосы → теги её членов (оси и блокировка).
    pub lanes: BTreeMap<String, Vec<String>>,
    /// Тег полосы, через которую идёт остальной трафик.
    pub default_lane_tag: String,
    /// Тег полосы → человеческое имя.
    pub lane_titles: BTreeMap<String, String>,
    /// Тег оси → теги маршрутов внутри неё.
    pub axes: BTreeMap<String, Vec<String>>,
    /// Тег маршрута → профиль, который за ним стоит.
    pub routes: BTreeMap<String, ServerProfile>,
    /// Адреса всех узлов — для пакетного фильтра: он принимает только литералы.
    pub hosts: Vec<String>,
    pub config: Value,
}

/// Собрать план по политике. Отказывает, если политику собирать нельзя.
pub fn build(
    profiles: &[ServerProfile],
    policy: Option<&LanePolicy>,
    options: &singbox::Options,
) -> Result<Plan, ValidationError> {
    if let Some(p) = policy {
        let available: BTreeSet<EvasionAxis> = profiles.iter().map(EvasionAxis::of).collect();
        p.validate(&available)?;
    }
    Ok(assemble(profiles, policy, options))
}

fn assemble(
    profiles: &[ServerProfile],
    policy: Option<&LanePolicy>,
    options: &singbox::Options,
) -> Plan {
    let mut route_outbounds: Vec<Value> = Vec::new();
    let mut routes: BTreeMap<String, ServerProfile> = BTreeMap::new();
    let mut by_axis: BTreeMap<EvasionAxis, Vec<String>> = BTreeMap::new();
    let mut hosts: Vec<String> = Vec::new();

    for (i, profile) in profiles.iter().enumerate() {
        let tag_i = route_tag(i);
        let mut o = singbox::outbound(profile);
        o["tag"] = json!(tag_i);
        // Адрес самой ноды разрешается МИМО туннеля. Иначе замыкание: чтобы
        // поднять туннель, надо узнать адрес узла, а чтобы узнать — нужен
        // поднятый туннель.
        o["domain_resolver"] = json!({ "server": tag::BOOTSTRAP_DNS });
        route_outbounds.push(o);
        routes.insert(tag_i.clone(), profile.clone());
        by_axis.entry(EvasionAxis::of(profile)).or_default().push(tag_i);
        if !hosts.contains(&profile.host) {
            hosts.push(profile.host.clone());
        }
    }

    // Ось — группа urltest. Нижний ярус автономии: даже если управляющий слой
    // умрёт, ядро само уйдёт с мёртвого маршрута внутри оси. Переоценивать это
    // нельзя — пробы там ленивые и история в одну запись, — но как страховка
    // это лучше, чем ничего.
    let mut axis_groups: Vec<Value> = Vec::new();
    let mut axis_map: BTreeMap<String, Vec<String>> = BTreeMap::new();
    for axis in EvasionAxis::ALL {
        let members = match by_axis.get(&axis) {
            Some(m) if !m.is_empty() => m.clone(),
            _ => continue,
        };
        let t = axis_tag(axis);
        axis_map.insert(t.clone(), members.clone());
        axis_groups.push(json!({
            "type": "urltest",
            "tag": t,
            "outbounds": members,
            "url": options.probe_url,
            "interval": "3m",
            // Порог улучшения у ядра грубый и по одному замеру. Нам он нужен
            // только чтобы ядро не металось само; настоящий гистерезис живёт
            // в управляющем слое.
            "tolerance": 100,
        }));
    }

    // Полоса — селектор. Состав селектора и есть множество достижимых через API
    // состояний: что не перечислено здесь, недостижимо.
    let default_spec = LanePolicy::factory();
    let specs: &[crate::policy::Lane] = match policy {
        Some(p) => &p.lanes,
        None => &default_spec.lanes[..1],
    };

    let mut lanes: BTreeMap<String, Vec<String>> = BTreeMap::new();
    let mut lane_titles: BTreeMap<String, String> = BTreeMap::new();
    let mut lane_selectors: Vec<Value> = Vec::new();

    for spec in specs {
        let t = lane_tag(&spec.id);
        let members: Vec<String> = if spec.allows_direct {
            // Полоса прямого выхода не содержит осей вовсе: у неё один
            // допустимый исход, и подменить его через API нельзя.
            vec![DIRECT.to_string()]
        } else {
            let mut m: Vec<String> = axis_map
                .keys()
                .filter(|t| match &spec.axis_in {
                    Some(allowed) => allowed.iter().any(|a| &axis_tag(*a) == *t),
                    None => true,
                })
                .cloned()
                .collect();
            m.push(BLOCK.to_string());
            m
        };
        lanes.insert(t.clone(), members.clone());
        lane_titles.insert(t.clone(), spec.title.clone());
        lane_selectors.push(json!({
            "type": "selector",
            "tag": t,
            "outbounds": members,
            "default": members.first().cloned().unwrap_or_else(|| BLOCK.to_string()),
            // Смена маршрута НЕ рвёт живые соединения: при плановом
            // переключении сессии дотекают по прежнему пути. Рвать их надо
            // только при аварии — отдельным запросом.
            "interrupt_exist_connections": false,
        }));
    }

    let mut outbounds: Vec<Value> = vec![
        json!({ "type": "block", "tag": BLOCK }),
        // Прямому выходу — СВОЙ резолвер, мимо туннеля. Две причины, обе
        // выяснились на живом прогоне. Первая: общий резолвер уведён внутрь
        // туннеля, и разрешение имени для прямого соединения через него не
        // работает. Вторая важнее: имя, разрешённое через выходной узел,
        // возвращает адрес чужого узла CDN, и российский сервис, ради которого
        // правило и написано, отвечает медленно или отказывает.
        json!({
            "type": "direct",
            "tag": DIRECT,
            "domain_resolver": { "server": tag::BOOTSTRAP_DNS },
        }),
    ];
    outbounds.extend(route_outbounds);
    outbounds.extend(axis_groups);
    outbounds.extend(lane_selectors);

    let config = json!({
        "log": { "level": options.log_level, "timestamp": true },
        "dns": dns(),
        "inbounds": [singbox::tun_inbound(options.mtu, &options.tun_stack)],
        "outbounds": outbounds,
        "route": route(&hosts, policy),
        "experimental": {
            "clash_api": {
                "external_controller": options.clash_controller,
                "secret": options.clash_secret,
            },
        },
    });

    let default_lane_tag = lane_tag(policy.map(|p| p.default_lane.as_str()).unwrap_or("web"));
    Plan {
        lanes,
        default_lane_tag,
        lane_titles,
        axes: axis_map,
        routes,
        hosts,
        config,
    }
}

fn dns() -> Value {
    json!({
        "servers": [
            // Резолвер внутри туннеля — через ПОЛОСУ, а не через конкретный
            // маршрут: иначе при смене маршрута DNS остался бы на старом.
            //
            // TCP, а не UDP, и это принципиально. Резолвер ходит ЧЕРЕЗ прокси,
            // а перенос UDP — необязательная способность цепочки: её должен
            // уметь и протокол, и транспорт. TCP умеют все и всегда. Ответы
            // DNS маленькие, лишний обмен на установку окупается тем, что
            // соединение живёт и переиспользуется.
            //
            // Проверено на живом Android 2026-08-24: по UDP резолвер падал
            // мгновенно — `write request: io: read/write on closed pipe` на
            // КАЖДОМ имени, при исправном туннеле и работающем трафике по
            // голому адресу. Со стороны выглядит как «интернета нет», хотя
            // виноват один только резолвер.
            { "tag": tag::REMOTE_DNS, "type": "tcp", "server": "1.1.1.1", "detour": DEFAULT_LANE_TAG },
            // Единственный резолвер мимо туннеля, и нужен ровно для одного:
            // узнать адрес узла до того, как туннель встал.
            { "tag": tag::BOOTSTRAP_DNS, "type": "udp", "server": "77.88.8.8" },
        ],
        "final": tag::REMOTE_DNS,
        "strategy": "ipv4_only",
    })
}

fn route(hosts: &[String], policy: Option<&LanePolicy>) -> Value {
    let mut rules: Vec<Value> = vec![
        json!({ "action": "sniff" }),
        json!({ "protocol": "dns", "action": "hijack-dns" }),
    ];

    // Трафик к самим узлам обязан идти напрямую, иначе туннель замкнётся сам
    // на себя.
    let literals: Vec<String> = hosts.iter().filter(|h| singbox::is_ipv4(h)).cloned().collect();
    if !literals.is_empty() {
        rules.push(json!({
            "ip_cidr": literals.iter().map(|h| format!("{h}/32")).collect::<Vec<_>>(),
            "outbound": DIRECT,
        }));
    }
    let domains: Vec<String> = hosts.iter().filter(|h| !singbox::is_ipv4(h)).cloned().collect();
    if !domains.is_empty() {
        rules.push(json!({ "domain": domains, "outbound": DIRECT }));
    }

    // Правила политики — в порядке написания, первое совпадение выигрывает.
    // Порядок ядра мы не переупорядочиваем: любая наша математика приоритетов
    // создала бы расхождение между тем, что человек читает, и тем, что
    // происходит.
    match policy {
        Some(p) => {
            for flow in &p.flows {
                let mut rule = match_json(&flow.matcher);
                rule["outbound"] = json!(lane_tag(&flow.lane));
                rules.push(rule);
            }
        }
        None => rules.push(json!({ "ip_is_private": true, "outbound": DIRECT })),
    }

    let final_lane = lane_tag(policy.map(|p| p.default_lane.as_str()).unwrap_or("web"));
    json!({
        "default_domain_resolver": { "server": tag::REMOTE_DNS },
        "rules": rules,
        // Второй эшелон тотальности: остаток всегда в полосе, а полоса остатка
        // по проверке политики не может выпускать открытым.
        "final": final_lane,
        "auto_detect_interface": true,
    })
}

fn match_json(m: &crate::policy::Match) -> Value {
    use crate::policy::Match;
    match m {
        Match::DomainSuffix(d) => json!({ "domain_suffix": d }),
        Match::DomainKeyword(d) => json!({ "domain_keyword": d }),
        Match::ProcessName(p) => json!({ "process_name": p }),
        Match::Port(p) => json!({ "port": p }),
        Match::IpIsPrivate => json!({ "ip_is_private": true }),
    }
}

/// Человеческое описание правила — для интерфейса и сообщений об отказе.
pub fn describe_match(m: &crate::policy::Match) -> String {
    describe(m)
}

#[cfg(test)]
mod tests {
    /// Стек TUN обязан попадать в конфиг из настроек, а не быть зашитым.
    ///
    /// Разница платформенная: на Android `system` не работает — приложению не
    /// дают перехватывать TCP, — и отказ выглядит не как отказ. UDP частично
    /// проходит, DNS-запросы видны, туннель поднят и рисует «Защищено», а TCP
    /// не доходит до ядра вовсе. Проверено на живом телефоне 2026-08-24:
    /// час ушёл на поиск, потому что журнал показывал исправную работу.
    #[test]
    fn stack_tunelya_beryotsya_iz_nastroek() {
        use crate::{singbox::Options, ServerProfile};
        let profile: ServerProfile =
            crate::parse("hysteria2://pass@1.2.3.4:443?insecure=1#узел").unwrap();

        let mut options = Options::new("s3cret");
        assert_eq!(options.tun_stack, "system", "по умолчанию — системный");

        options.tun_stack = "gvisor".into();
        let plan = super::build(&[profile], None, &options).unwrap();
        let stack = plan.config["inbounds"][0]["stack"].as_str().unwrap();
        assert_eq!(stack, "gvisor", "настройка обязана доходить до конфига");
    }

    use super::*;
    use crate::link;

    fn profiles() -> Vec<ServerProfile> {
        [
            "hysteria2://pw@1.2.3.4:443?insecure=1#US-hy2",
            "vless://u@1.2.3.4:2083?security=reality&pbk=K&sni=a.com&type=grpc&serviceName=gs#US-grpc",
            "vless://u@1.2.3.4:2087?security=reality&pbk=K&sni=a.com&flow=xtls-rprx-vision#US-vision",
            "trojan://pw@sub.example.com:8443?security=tls#LT-trojan",
        ]
        .iter()
        .map(|l| link::parse(l).unwrap())
        .collect()
    }

    #[test]
    fn оси_считаются_по_способу_а_не_по_протоколу() {
        let ps = profiles();
        assert_eq!(EvasionAxis::of(&ps[1]), EvasionAxis::FakeTlsH2);
        assert_eq!(EvasionAxis::of(&ps[2]), EvasionAxis::FakeTlsTcp);
        // Один протокол VLESS, а оси разные — их ломает разное.
        assert_ne!(EvasionAxis::of(&ps[1]), EvasionAxis::of(&ps[2]));
    }

    #[test]
    fn полоса_содержит_блокировку_и_никогда_прямой_выход() {
        let plan = build(&profiles(), Some(&LanePolicy::factory()), &singbox::Options::new("s")).unwrap();
        let web = &plan.lanes["L.web"];
        assert!(web.contains(&"OUT_block".to_string()), "нет блокировки в составе полосы");
        assert!(!web.contains(&"OUT_direct".to_string()), "прямой выход достижим через API");
    }

    #[test]
    fn прямая_полоса_содержит_только_прямой_выход() {
        let plan = build(&profiles(), Some(&LanePolicy::factory()), &singbox::Options::new("s")).unwrap();
        assert_eq!(plan.lanes["L.lan"], vec!["OUT_direct".to_string()]);
    }

    #[test]
    fn остаток_всегда_уходит_в_полосу() {
        let plan = build(&profiles(), Some(&LanePolicy::factory()), &singbox::Options::new("s")).unwrap();
        assert_eq!(plan.config["route"]["final"], "L.web");
    }

    #[test]
    fn политика_без_запаса_на_оси_не_собирается() {
        let only_hy2 = vec![link::parse("hysteria2://pw@1.2.3.4:443#H").unwrap()];
        let e = build(&only_hy2, Some(&LanePolicy::factory()), &singbox::Options::new("s")).unwrap_err();
        assert!(matches!(e, ValidationError::NotEnoughAxes { .. }));
    }

    #[test]
    fn резолвер_туннеля_ходит_через_полосу_а_не_через_маршрут() {
        let plan = build(&profiles(), Some(&LanePolicy::factory()), &singbox::Options::new("s")).unwrap();
        // Иначе при смене маршрута DNS остался бы на старом.
        assert_eq!(plan.config["dns"]["servers"][0]["detour"], "L.web");
    }
}
