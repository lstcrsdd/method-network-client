//! Сборка конфига sing-box из профиля сервера.
//!
//! Порт со Swift-версии (`MacOS/Shared/Core/SingBoxConfigBuilder.swift`),
//! сделанный ПОБАЙТОВО совпадающим: расхождение здесь означало бы, что клиенты
//! на разных платформах поднимают разные туннели из одной и той же ссылки.
//!
//! Схема выверена против sing-box 1.13.x:
//!   * `route.default_domain_resolver` обязателен (миграция 1.12);
//!   * DNS-серверы в новом формате (`type`/`server`);
//!   * sniffing и DNS-hijack — это route-actions, а не поля inbound.

use crate::profile::*;
use serde_json::{json, Map, Value};

pub mod tag {
    pub const PROXY: &str = "proxy";
    pub const DIRECT: &str = "direct";
    /// Резолвер внутри туннеля — всё, что ядро разрешает само.
    pub const REMOTE_DNS: &str = "remote";
    /// Резолвер мимо туннеля — ТОЛЬКО адрес самой ноды.
    pub const BOOTSTRAP_DNS: &str = "bootstrap";
}

#[derive(Debug, Clone)]
pub struct Options {
    pub clash_controller: String,
    pub clash_secret: String,
    pub log_level: String,
    /// MTU для utun. 1380, а не 1400: наружу пакет уходит внутри QUIC-датаграммы
    /// вместе с заголовками Hysteria2/QUIC/UDP/IP, и при 1400 итог не пролезал
    /// в путь мобильного оператора — соединение флапало, DNS не укладывался в
    /// таймаут. 1380 оставляет около семидесяти байт запаса.
    pub mtu: u32,
    /// Адрес проверки. ТОЛЬКО https: ядро игнорирует http-адрес и молча
    /// подставляет собственный на gstatic — измерение уехало бы не туда.
    pub probe_url: String,
    /// Стек TUN. Платформенная разница, а не вкусовая настройка.
    ///
    /// `system` разбирает пакеты средствами ядра ОС и быстрее, но требует
    /// прав уровня системы: на macOS их даёт привилегированный демон.
    /// На Android их нет и быть не может — приложению не позволят
    /// перехватывать TCP, — поэтому там нужен `gvisor`, разбирающий пакеты
    /// сам, внутри процесса.
    ///
    /// Ошибка здесь не выглядит ошибкой: с `system` на Android UDP частично
    /// проходит, DNS-запросы видны в журнале, туннель поднят и показывает
    /// «Защищено», а TCP не доходит до ядра ВОВСЕ — ни одной строки о
    /// соединении. Снаружи читается как «узлы не отвечают».
    pub tun_stack: String,
}

impl Options {
    pub fn new(clash_secret: impl Into<String>) -> Self {
        Options {
            clash_controller: "127.0.0.1:9090".into(),
            clash_secret: clash_secret.into(),
            log_level: "info".into(),
            mtu: 1380,
            probe_url: "https://www.gstatic.com/generate_204".into(),
            tun_stack: "system".into(),
        }
    }
}

/// Конфиг одиночного маршрута для режима TUN.
pub fn build(profile: &ServerProfile, options: &Options) -> Value {
    // Адрес самой ноды разрешается ОТДЕЛЬНЫМ резолвером, мимо туннеля. Иначе
    // замыкание: чтобы поднять туннель, надо узнать адрес ноды (у Trojan это
    // домен), а чтобы узнать адрес — нужен поднятый туннель.
    let mut proxy = outbound(profile);
    proxy["domain_resolver"] = json!({ "server": tag::BOOTSTRAP_DNS });

    json!({
        "log": { "level": options.log_level, "timestamp": true },
        "dns": dns(),
        "inbounds": [tun_inbound(options.mtu, &options.tun_stack)],
        "outbounds": [proxy, { "type": "direct", "tag": tag::DIRECT }],
        "route": route(profile),
        "experimental": {
            "clash_api": {
                "external_controller": options.clash_controller,
                "secret": options.clash_secret,
            }
        },
    })
}

/// Локальный mixed-конфиг для проверки протокола до поднятия системного TUN.
pub fn build_probe(profile: &ServerProfile, listen_port: u16, bind_interface: Option<&str>) -> Value {
    let mut proxy = outbound(profile);
    if let Some(iface) = bind_interface.filter(|s| !s.is_empty()) {
        proxy["bind_interface"] = json!(iface);
    }
    json!({
        "log": { "level": "error", "timestamp": true },
        "inbounds": [{
            "type": "mixed",
            "tag": "probe-in",
            "listen": "127.0.0.1",
            "listen_port": listen_port,
        }],
        "outbounds": [proxy],
        "route": { "final": tag::PROXY },
    })
}

pub fn dns() -> Value {
    json!({
        "servers": [
            // Обычный UDP внутрь туннеля, а не DNS-over-TLS. DoT держит
            // долгоживущий TCP-сеанс, и внутри QUIC он залипал: соединение к
            // 1.1.1.1:853 висело по 16–43 секунды и валилось в «context
            // deadline exceeded». Приватность не страдает — запрос идёт внутри
            // шифрованного канала.
            { "tag": tag::REMOTE_DNS, "type": "udp", "server": "1.1.1.1", "detour": tag::PROXY },
            // Единственный резолвер мимо туннеля, и нужен ровно для одного:
            // узнать адрес самой ноды до того, как туннель встал. Ссылается на
            // него ТОЛЬКО domain_resolver у proxy-outbound.
            //
            // Яндекс, а не 223.5.5.5: тот — AliDNS в Китае, из России до него
            // далеко. `detour` тут ставить нельзя — ядро отвечает FATAL «detour
            // to an empty direct outbound makes no sense».
            { "tag": tag::BOOTSTRAP_DNS, "type": "udp", "server": "77.88.8.8" },
        ],
        "final": tag::REMOTE_DNS,
        "strategy": "ipv4_only",
    })
}

pub fn tun_inbound(mtu: u32, stack: &str) -> Value {
    json!({
        "type": "tun",
        "tag": "tun-in",
        // 198.18.0.0/15 — диапазон RFC 2544 под тестирование оборудования.
        // Реальные сети им не пользуются. Было 172.18.0.1/30, и это ломало
        // клиент в корпоративных сетях: офисный роутер раздавал маршрут на весь
        // 172.16/12, наш адрес оказывался внутри чужого маршрута, и туннель не
        // вставал — снаружи при этом всё работало.
        "address": ["198.18.2.1/30"],
        "mtu": mtu,
        "auto_route": true,
        "strict_route": true,
        "stack": stack,
    })
}

fn route(profile: &ServerProfile) -> Value {
    json!({
        // Всё, что ядро разрешает САМО, уходит внутрь туннеля. Было `local` —
        // резолвер без detour: при поднятом туннеле имена уходили открытым UDP
        // по физическому интерфейсу.
        "default_domain_resolver": { "server": tag::REMOTE_DNS },
        "rules": [
            { "action": "sniff" },
            { "protocol": "dns", "action": "hijack-dns" },
            server_direct_rule(&profile.host),
            { "ip_is_private": true, "outbound": tag::DIRECT },
        ],
        "final": tag::PROXY,
        "auto_detect_interface": true,
    })
}

/// Трафик к самой ноде обязан идти напрямую, иначе туннель замкнётся сам на себя.
pub fn server_direct_rule(host: &str) -> Value {
    if host.contains(':') {
        return json!({ "ip_cidr": [format!("{host}/128")], "outbound": tag::DIRECT });
    }
    if is_ipv4(host) {
        return json!({ "ip_cidr": [format!("{host}/32")], "outbound": tag::DIRECT });
    }
    json!({ "domain": [host], "outbound": tag::DIRECT })
}

pub fn is_ipv4(host: &str) -> bool {
    let parts: Vec<&str> = host.split('.').collect();
    parts.len() == 4
        && parts
            .iter()
            .all(|p| !p.is_empty() && p.parse::<u16>().map(|o| o <= 255).unwrap_or(false))
}

// ─────────────────────────── Outbound по протоколу ───────────────────────────

/// Outbound одного маршрута. Открыт наружу: он одинаков и в одиночной схеме, и
/// в схеме полос, и дублировать его значило бы разъехаться при первой правке.
pub fn outbound(profile: &ServerProfile) -> Value {
    match &profile.parameters {
        Parameters::Hysteria2 { params } => hysteria2_outbound(profile, params),
        Parameters::VlessReality { params } => vless_outbound(profile, params),
        Parameters::Trojan { params } => trojan_outbound(profile, params),
        Parameters::Shadowsocks { params } => shadowsocks_outbound(profile, params),
    }
}

fn hysteria2_outbound(profile: &ServerProfile, p: &Hysteria2) -> Value {
    let mut out = json!({
        "type": "hysteria2",
        "tag": tag::PROXY,
        "server": profile.host,
        "server_port": profile.port,
        "password": p.password,
        "tls": {
            "enabled": true,
            "server_name": p.sni.clone().unwrap_or_else(|| profile.host.clone()),
            "insecure": p.allow_insecure,
            "alpn": p.alpn,
        },
    });
    if let Some(up) = p.up_mbps {
        out["up_mbps"] = json!(up);
    }
    if let Some(down) = p.down_mbps {
        out["down_mbps"] = json!(down);
    }
    if let Some(obfs) = &p.obfs_password {
        out["obfs"] = json!({ "type": "salamander", "password": obfs });
    }
    // Прыжки по портам намеренно НЕ включаются, хотя поля для них есть в
    // модели. Замер 2026-07-30, по 4 запроса на каждый из шести инстансов: с
    // прыжками 0–2 из 4, на фиксированном порту 4 из 4 везде. За NAT смена
    // порта раз в 30 с плодит записи трансляции быстрее, чем роутер успевает
    // их удерживать.
    out
}

fn vless_outbound(profile: &ServerProfile, p: &VlessReality) -> Value {
    let mut out = json!({
        "type": "vless",
        "tag": tag::PROXY,
        "server": profile.host,
        "server_port": profile.port,
        "uuid": p.uuid,
        "packet_encoding": "xudp",
        "tls": {
            "enabled": true,
            "server_name": p.sni,
            "utls": { "enabled": true, "fingerprint": p.fingerprint },
            "reality": {
                "enabled": true,
                "public_key": p.public_key,
                "short_id": p.short_id,
            },
        },
    });
    match (&p.grpc_service_name, &p.flow) {
        (Some(service), _) if !service.is_empty() => {
            // gRPC несовместим с flow: Vision работает только поверх голого TCP,
            // вместе они дают отказ аутентификации на стороне Xray — соединение
            // принимается, а данные не идут.
            out["transport"] = json!({ "type": "grpc", "service_name": service });
        }
        (_, Some(flow)) if !flow.is_empty() => {
            out["flow"] = json!(flow);
        }
        _ => {}
    }
    out
}

fn trojan_outbound(profile: &ServerProfile, p: &Trojan) -> Value {
    let mut tls = Map::new();
    tls.insert("enabled".into(), json!(true));
    tls.insert(
        "server_name".into(),
        json!(p.sni.clone().unwrap_or_else(|| profile.host.clone())),
    );
    tls.insert("insecure".into(), json!(p.allow_insecure));
    if let Some(fp) = p.fingerprint.as_deref().filter(|s| !s.is_empty()) {
        tls.insert("utls".into(), json!({ "enabled": true, "fingerprint": fp }));
    }

    let mut out = json!({
        "type": "trojan",
        "tag": tag::PROXY,
        "server": profile.host,
        "server_port": profile.port,
        "password": p.password,
        "tls": Value::Object(tls),
    });
    if let Some(service) = p.grpc_service_name.as_deref().filter(|s| !s.is_empty()) {
        out["transport"] = json!({ "type": "grpc", "service_name": service });
    } else if let Some(path) = p.ws_path.as_deref().filter(|s| !s.is_empty()) {
        let mut ws = json!({ "type": "ws", "path": path });
        if let Some(host) = p.ws_host.as_deref().filter(|s| !s.is_empty()) {
            ws["headers"] = json!({ "Host": host });
        }
        out["transport"] = ws;
    }
    out
}

fn shadowsocks_outbound(profile: &ServerProfile, p: &Shadowsocks) -> Value {
    json!({
        "type": "shadowsocks",
        "tag": tag::PROXY,
        "server": profile.host,
        "server_port": profile.port,
        "method": p.method,
        "password": p.password,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::link;

    fn built(link_str: &str) -> Value {
        let p = link::parse(link_str).unwrap();
        build(&p, &Options::new("secret"))
    }

    #[test]
    fn адрес_ноды_разрешается_мимо_туннеля() {
        let c = built("trojan://pw@sub.example.com:8443?security=tls#T");
        let proxy = &c["outbounds"][0];
        assert_eq!(proxy["domain_resolver"]["server"], "bootstrap");
        // И этот резолвер существует ровно один и без detour: с detour ядро
        // отвечает FATAL.
        let boot = &c["dns"]["servers"][1];
        assert_eq!(boot["tag"], "bootstrap");
        assert!(boot.get("detour").is_none());
    }

    #[test]
    fn трафик_к_ноде_идёт_напрямую() {
        let c = built("hysteria2://pw@1.2.3.4:443#N");
        let rules = c["route"]["rules"].as_array().unwrap();
        assert!(rules.iter().any(|r| r["ip_cidr"][0] == "1.2.3.4/32" && r["outbound"] == "direct"));
    }

    #[test]
    fn доменный_узел_попадает_в_правило_по_имени() {
        let c = built("trojan://pw@sub.example.com:8443#T");
        let rules = c["route"]["rules"].as_array().unwrap();
        assert!(rules.iter().any(|r| r["domain"][0] == "sub.example.com"));
    }

    #[test]
    fn grpc_исключает_flow() {
        let c = built("vless://u@1.2.3.4:2083?security=reality&pbk=K&sni=a.com&type=grpc&serviceName=gs&flow=xtls-rprx-vision#G");
        let proxy = &c["outbounds"][0];
        assert_eq!(proxy["transport"]["type"], "grpc");
        assert!(proxy.get("flow").is_none(), "flow вместе с gRPC ломает аутентификацию");
    }

    #[test]
    fn прыжки_не_попадают_в_конфиг() {
        let c = built("hysteria2://pw@1.2.3.4:443,36000-50000/?insecure=1#H");
        let proxy = &c["outbounds"][0];
        assert!(proxy.get("server_ports").is_none());
        assert_eq!(proxy["server_port"], 443);
    }
}
