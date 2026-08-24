//! Разбор share-ссылок. Одна реализация на macOS, Android и Windows.
//!
//! Порт со Swift-версии (`MacOS/Shared/Models/ShareLinkParser.swift`), сделанный
//! ПОВЕДЕНЧЕСКИ ОДИНАКОВЫМ: те же умолчания, те же псевдонимы параметров, те же
//! отказы. Расхождение здесь означало бы, что клиент на одной платформе
//! принимает ссылку, а на другой молча её отвергает, — ровно то, ради чего
//! общий код и затевался.

use crate::marker;
use crate::profile::*;
use base64::Engine;
use url::Url;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ParseError {
    MalformedUrl,
    UnsupportedScheme(String),
    UnsupportedTransport(String),
    MissingField(&'static str),
}

impl std::fmt::Display for ParseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ParseError::MalformedUrl => write!(f, "ссылка не разбирается"),
            ParseError::UnsupportedScheme(s) => write!(f, "неизвестная схема: {s}"),
            ParseError::UnsupportedTransport(t) => write!(f, "неподдерживаемый транспорт: {t}"),
            ParseError::MissingField(x) => write!(f, "не хватает поля: {x}"),
        }
    }
}

impl std::error::Error for ParseError {}

/// Разобрать одну ссылку.
pub fn parse(raw: &str) -> Result<ServerProfile, ParseError> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return Err(ParseError::MalformedUrl);
    }
    let lower = trimmed.to_lowercase();

    // Shadowsocks разбирается вручную: у него две несовместимые формы, и обе
    // ломают обычный разбор URL.
    if lower.starts_with("ss://") {
        return parse_shadowsocks(trimmed);
    }

    let prepared = strip_port_hopping(trimmed);
    let url = Url::parse(&prepared).map_err(|_| ParseError::MalformedUrl)?;

    match url.scheme() {
        "hysteria2" | "hy2" => parse_hysteria2(&url),
        "vless" => parse_vless(&url),
        "trojan" => parse_trojan(&url),
        other => Err(ParseError::UnsupportedScheme(other.to_string())),
    }
}

/// Разобрать пачку строк: возвращает профили и номера строк с ошибками.
pub fn parse_many(raw: &str) -> (Vec<ServerProfile>, Vec<(usize, ParseError)>) {
    let mut profiles = Vec::new();
    let mut errors = Vec::new();
    for (i, line) in raw.lines().enumerate() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        match parse(line) {
            Ok(p) => profiles.push(p),
            Err(e) => errors.push((i + 1, e)),
        }
    }
    (profiles, errors)
}

/// Hysteria2 умеет прыжки по портам, и в ссылке это пишется как
/// `host:PORT,START-END` — авторитет становится невалидным, и URL не
/// разбирается вовсе. Диапазон вырезаем, оставляя базовый порт.
///
/// Прыжки при этом НЕ включаются намеренно: за NAT они рвут связь. Замерено
/// 2026-07-30 — на фиксированном порту 4 из 4 запросов, с прыжками 1 из 4.
/// Роутер не успевает удерживать записи трансляции, которые меняются раз в 30 с.
fn strip_port_hopping(raw: &str) -> String {
    let bytes: Vec<char> = raw.chars().collect();
    let mut out = String::with_capacity(raw.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == ':' {
            // :ЦИФРЫ,ЦИФРЫ-ЦИФРЫ
            let mut j = i + 1;
            let p1 = take_digits(&bytes, &mut j, 5);
            if p1 > 0 && j < bytes.len() && bytes[j] == ',' {
                let mut k = j + 1;
                let p2 = take_digits(&bytes, &mut k, 5);
                if p2 > 0 && k < bytes.len() && bytes[k] == '-' {
                    let mut m = k + 1;
                    let p3 = take_digits(&bytes, &mut m, 5);
                    if p3 > 0 {
                        out.push(':');
                        out.extend(&bytes[i + 1..i + 1 + p1]);
                        i = m;
                        continue;
                    }
                }
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    out
}

fn take_digits(chars: &[char], idx: &mut usize, max: usize) -> usize {
    let start = *idx;
    while *idx < chars.len() && chars[*idx].is_ascii_digit() && *idx - start < max {
        *idx += 1;
    }
    *idx - start
}

// ─────────────────────────────── Hysteria2 ───────────────────────────────

fn parse_hysteria2(u: &Url) -> Result<ServerProfile, ParseError> {
    let host = host_of(u)?;
    let port = u.port().unwrap_or(443);
    let password = user_info(u);
    if password.is_empty() {
        return Err(ParseError::MissingField("password"));
    }

    let q = query(u);
    let obfs_type = get(&q, "obfs").map(|s| s.to_lowercase());
    let obfs_password = if obfs_type.as_deref() == Some("salamander") {
        empty_to_none(get(&q, "obfs-password"))
    } else {
        None
    };

    let params = Hysteria2 {
        password,
        sni: get(&q, "sni").or_else(|| get(&q, "peer")),
        allow_insecure: bool_flag(get(&q, "insecure")),
        obfs_password,
        up_mbps: get(&q, "upmbps").and_then(|v| v.parse().ok()),
        down_mbps: get(&q, "downmbps").and_then(|v| v.parse().ok()),
        alpn: get(&q, "alpn")
            .map(|v| v.split(',').map(|s| s.to_string()).collect())
            .unwrap_or_else(|| vec!["h3".to_string()]),
        server_ports: None,
        hop_interval: None,
    };
    let (name, label) = naming(u, &host);
    Ok(ServerProfile {
        name,
        proto: VpnProtocol::Hysteria2,
        host,
        port,
        parameters: Parameters::Hysteria2 { params },
        featured_label: label,
    })
}

// ──────────────────────────── VLESS + Reality ────────────────────────────

fn parse_vless(u: &Url) -> Result<ServerProfile, ParseError> {
    let host = host_of(u)?;
    let port = u.port().ok_or(ParseError::MissingField("port"))?;
    let uuid = user_info(u);
    if uuid.is_empty() {
        return Err(ParseError::MissingField("uuid"));
    }

    let q = query(u);
    if get(&q, "security").map(|s| s.to_lowercase()).as_deref() != Some("reality") {
        return Err(ParseError::MissingField("security=reality"));
    }
    let pbk = empty_to_none(get(&q, "pbk")).ok_or(ParseError::MissingField("pbk (public key)"))?;
    let sni = empty_to_none(get(&q, "sni").or_else(|| get(&q, "peer")))
        .ok_or(ParseError::MissingField("sni"))?;

    // Транспорт читается обязательно. Пока он игнорировался, gRPC-ссылка молча
    // разбиралась как голый TCP: запись добавлялась, выглядела рабочей и не
    // подключалась никогда.
    let transport = get(&q, "type").unwrap_or_else(|| "tcp".into()).to_lowercase();
    let service = empty_to_none(get(&q, "servicename"));
    match transport.as_str() {
        "tcp" | "" => {}
        "grpc" => {
            if service.is_none() {
                return Err(ParseError::MissingField("serviceName (для gRPC)"));
            }
        }
        other => return Err(ParseError::UnsupportedTransport(other.to_string())),
    }

    let params = VlessReality {
        uuid,
        // gRPC несовместим с flow: Vision работает только поверх голого TCP,
        // вместе они дают отказ аутентификации на стороне Xray.
        flow: if transport == "grpc" {
            None
        } else {
            empty_to_none(get(&q, "flow"))
        },
        sni,
        public_key: pbk,
        short_id: get(&q, "sid").unwrap_or_default(),
        fingerprint: get(&q, "fp").unwrap_or_else(|| "chrome".into()),
        grpc_service_name: service,
    };
    let (name, label) = naming(u, &host);
    Ok(ServerProfile {
        name,
        proto: VpnProtocol::VlessReality,
        host,
        port,
        parameters: Parameters::VlessReality { params },
        featured_label: label,
    })
}

// ───────────────────────────────── Trojan ─────────────────────────────────

fn parse_trojan(u: &Url) -> Result<ServerProfile, ParseError> {
    let host = host_of(u)?;
    let port = u.port().unwrap_or(443);
    let password = user_info(u);
    if password.is_empty() {
        return Err(ParseError::MissingField("password"));
    }

    let q = query(u);
    let transport = get(&q, "type").unwrap_or_else(|| "tcp".into()).to_lowercase();
    let mut service = None;
    let mut ws_path = None;
    let mut ws_host = None;
    match transport.as_str() {
        "tcp" | "" => {}
        "grpc" => {
            service = empty_to_none(get(&q, "servicename"));
            if service.is_none() {
                return Err(ParseError::MissingField("serviceName (для gRPC)"));
            }
        }
        "ws" => {
            ws_path = Some(empty_to_none(get(&q, "path")).unwrap_or_else(|| "/".into()));
            ws_host = empty_to_none(get(&q, "host"));
        }
        other => return Err(ParseError::UnsupportedTransport(other.to_string())),
    }

    let params = Trojan {
        password,
        // Trojan держится на ПОДЛИННОСТИ TLS: sni обязан совпадать с именем в
        // сертификате, иначе соединение не встанет. Поэтому при отсутствии
        // берём host, а не оставляем пустым.
        sni: Some(empty_to_none(get(&q, "sni").or_else(|| get(&q, "peer"))).unwrap_or_else(|| host.clone())),
        allow_insecure: bool_flag(get(&q, "allowinsecure").or_else(|| get(&q, "insecure"))),
        fingerprint: empty_to_none(get(&q, "fp")),
        grpc_service_name: service,
        ws_path,
        ws_host,
    };
    let (name, label) = naming(u, &host);
    Ok(ServerProfile {
        name,
        proto: VpnProtocol::Trojan,
        host,
        port,
        parameters: Parameters::Trojan { params },
        featured_label: label,
    })
}

// ─────────────────────────────── Shadowsocks ───────────────────────────────

/// Две несовместимые формы, обе встречаются в живых подписках:
///   SIP002: `ss://base64url(method:password)@host:port#name`
///   старая: `ss://base64(method:password@host:port)#name`
fn parse_shadowsocks(raw: &str) -> Result<ServerProfile, ParseError> {
    let mut body = &raw["ss://".len()..];

    // Имя за решёткой отрезаем сразу: в старой форме оно не входит в base64.
    let mut name: Option<String> = None;
    if let Some(hash) = body.find('#') {
        name = Some(percent_decode(&body[hash + 1..]));
        body = &body[..hash];
    }
    if let Some(qm) = body.find('?') {
        body = &body[..qm];
    }
    if body.is_empty() {
        return Err(ParseError::MalformedUrl);
    }

    let (user_info, host_port) = if let Some(at) = body.rfind('@') {
        let mut ui = body[..at].to_string();
        let hp = body[at + 1..].to_string();
        if !ui.contains(':') {
            if let Some(decoded) = decode_base64(&ui) {
                ui = decoded;
            }
        }
        (ui, hp)
    } else {
        let decoded = decode_base64(body).ok_or(ParseError::MalformedUrl)?;
        let at = decoded.rfind('@').ok_or(ParseError::MalformedUrl)?;
        (decoded[..at].to_string(), decoded[at + 1..].to_string())
    };

    let colon = user_info
        .find(':')
        .ok_or(ParseError::MissingField("method:password"))?;
    let method = user_info[..colon].to_string();
    let password = user_info[colon + 1..].to_string();
    if method.is_empty() {
        return Err(ParseError::MissingField("method"));
    }
    if password.is_empty() {
        return Err(ParseError::MissingField("password"));
    }

    // Хост может быть IPv6 в скобках — тогда двоеточий много, делим по последнему.
    let port_colon = host_port
        .rfind(':')
        .ok_or(ParseError::MissingField("port"))?;
    let host = host_port[..port_colon]
        .trim_matches(|c| c == '[' || c == ']')
        .to_string();
    let port: u16 = host_port[port_colon + 1..]
        .parse()
        .map_err(|_| ParseError::MissingField("host:port"))?;
    if host.is_empty() {
        return Err(ParseError::MissingField("host:port"));
    }

    // Маркер ищем только в имени: host задаёт инфраструктура, а не провайдер.
    let (final_name, label) = match name {
        Some(n) => marker::strip(&n),
        None => (host.clone(), None),
    };
    Ok(ServerProfile {
        name: final_name,
        proto: VpnProtocol::Shadowsocks,
        host,
        port,
        parameters: Parameters::Shadowsocks {
            params: Shadowsocks { method, password },
        },
        featured_label: label,
    })
}

// ──────────────────────────────── Помощники ────────────────────────────────

fn host_of(u: &Url) -> Result<String, ParseError> {
    match u.host_str() {
        Some(h) if !h.is_empty() => Ok(h.trim_matches(|c| c == '[' || c == ']').to_string()),
        _ => Err(ParseError::MissingField("host")),
    }
}

/// Параметры запроса. Имена приводятся к нижнему регистру: провайдеры пишут и
/// `serviceName`, и `servicename`, и обе формы обязаны работать.
fn query(u: &Url) -> Vec<(String, String)> {
    u.query_pairs()
        .map(|(k, v)| (k.to_lowercase(), v.to_string()))
        .collect()
}

fn get(q: &[(String, String)], key: &str) -> Option<String> {
    q.iter().find(|(k, _)| k == key).map(|(_, v)| v.clone())
}

/// user-info часть URL (`user[:password]@`), percent-декодированная.
fn user_info(u: &Url) -> String {
    let mut s = percent_decode(u.username());
    if let Some(pw) = u.password() {
        if !pw.is_empty() {
            s.push(':');
            s.push_str(&percent_decode(pw));
        }
    }
    s
}

fn naming(u: &Url, host: &str) -> (String, Option<String>) {
    match u.fragment() {
        Some(f) if !f.is_empty() => marker::strip(&percent_decode(f)),
        _ => (host.to_string(), None),
    }
}

fn percent_decode(s: &str) -> String {
    percent_encoding_decode(s).unwrap_or_else(|| s.to_string())
}

fn percent_encoding_decode(s: &str) -> Option<String> {
    let bytes = s.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            let hex = std::str::from_utf8(&bytes[i + 1..i + 3]).ok()?;
            match u8::from_str_radix(hex, 16) {
                Ok(b) => {
                    out.push(b);
                    i += 3;
                    continue;
                }
                Err(_) => return None,
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8(out).ok()
}

fn bool_flag(v: Option<String>) -> bool {
    match v.map(|s| s.to_lowercase()) {
        Some(s) => s == "1" || s == "true" || s == "yes",
        None => false,
    }
}

fn empty_to_none(s: Option<String>) -> Option<String> {
    s.filter(|v| !v.is_empty())
}

/// base64 из ссылок бывает и обычный, и url-safe, и без выравнивания.
fn decode_base64(s: &str) -> Option<String> {
    let mut t = s.replace('-', "+").replace('_', "/");
    while t.len() % 4 != 0 {
        t.push('=');
    }
    let bytes = base64::engine::general_purpose::STANDARD.decode(t).ok()?;
    String::from_utf8(bytes).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hysteria2_с_обфускацией() {
        let p = parse("hysteria2://shared:pass@1.2.3.4:443/?sni=www.microsoft.com&obfs=salamander&obfs-password=xyz&insecure=1#%F0%9F%87%AB%F0%9F%87%AE%20FI").unwrap();
        assert_eq!(p.proto, VpnProtocol::Hysteria2);
        assert_eq!(p.host, "1.2.3.4");
        assert_eq!(p.port, 443);
        let Parameters::Hysteria2 { params } = &p.parameters else { panic!() };
        assert_eq!(params.password, "shared:pass");
        assert_eq!(params.sni.as_deref(), Some("www.microsoft.com"));
        assert!(params.allow_insecure);
        assert_eq!(params.obfs_password.as_deref(), Some("xyz"));
        assert_eq!(params.alpn, vec!["h3"]);
        assert_eq!(p.name, "🇫🇮 FI");
    }

    #[test]
    fn прыжки_по_портам_вырезаются_из_ссылки() {
        // Полосу срезаем, порт остаётся базовым: за NAT прыжки рвут связь.
        let p = parse("hysteria2://pw@5.6.7.8:443,36000-50000/?insecure=1#N").unwrap();
        assert_eq!(p.port, 443);
        let Parameters::Hysteria2 { params } = &p.parameters else { panic!() };
        assert!(params.server_ports.is_none(), "полоса прыжков просочилась в профиль");
    }

    #[test]
    fn обфускация_только_у_саламандры() {
        let p = parse("hysteria2://pw@1.2.3.4:443/?obfs=none&obfs-password=xyz#N").unwrap();
        let Parameters::Hysteria2 { params } = &p.parameters else { panic!() };
        assert!(params.obfs_password.is_none());
    }

    #[test]
    fn vless_grpc_отличается_от_vision() {
        let vision = parse("vless://uuid-1@1.2.3.4:2087?security=reality&pbk=KEY&sni=a.com&fp=chrome&flow=xtls-rprx-vision#V").unwrap();
        let Parameters::VlessReality { params } = &vision.parameters else { panic!() };
        assert_eq!(params.flow.as_deref(), Some("xtls-rprx-vision"));
        assert!(params.grpc_service_name.is_none());

        let grpc = parse("vless://uuid-1@1.2.3.4:2083?security=reality&pbk=KEY&sni=a.com&type=grpc&serviceName=gs-fi#G").unwrap();
        let Parameters::VlessReality { params } = &grpc.parameters else { panic!() };
        assert_eq!(params.grpc_service_name.as_deref(), Some("gs-fi"));
        // flow и gRPC вместе дают отказ аутентификации на стороне Xray.
        assert!(params.flow.is_none());
    }

    #[test]
    fn vless_без_reality_отвергается() {
        let e = parse("vless://uuid@1.2.3.4:443?security=tls&pbk=K&sni=a.com#N").unwrap_err();
        assert_eq!(e, ParseError::MissingField("security=reality"));
    }

    #[test]
    fn grpc_без_имени_сервиса_отвергается() {
        let e = parse("vless://uuid@1.2.3.4:2083?security=reality&pbk=K&sni=a.com&type=grpc#N").unwrap_err();
        assert_eq!(e, ParseError::MissingField("serviceName (для gRPC)"));
    }

    #[test]
    fn неизвестный_транспорт_отвергается_а_не_принимается_молча() {
        let e = parse("vless://uuid@1.2.3.4:2083?security=reality&pbk=K&sni=a.com&type=kcp#N").unwrap_err();
        assert_eq!(e, ParseError::UnsupportedTransport("kcp".into()));
    }

    #[test]
    fn trojan_берёт_sni_из_хоста_когда_не_указан() {
        let p = parse("trojan://pw@sub.example.com:8443?security=tls#T").unwrap();
        let Parameters::Trojan { params } = &p.parameters else { panic!() };
        assert_eq!(params.sni.as_deref(), Some("sub.example.com"));
    }

    #[test]
    fn shadowsocks_sip002() {
        let ui = base64::engine::general_purpose::STANDARD.encode("2022-blake3-aes-128-gcm:KEY==");
        let link = format!("ss://{ui}@1.2.3.4:2095#LT");
        let p = parse(&link).unwrap();
        assert_eq!(p.proto, VpnProtocol::Shadowsocks);
        assert_eq!(p.port, 2095);
        let Parameters::Shadowsocks { params } = &p.parameters else { panic!() };
        assert_eq!(params.method, "2022-blake3-aes-128-gcm");
        // Ключ семейства 2022 нормализовать нельзя — проверяем побайтово.
        assert_eq!(params.password, "KEY==");
    }

    #[test]
    fn shadowsocks_старая_форма() {
        let all = base64::engine::general_purpose::STANDARD.encode("aes-256-gcm:pw@9.9.9.9:8388");
        let p = parse(&format!("ss://{all}#Old")).unwrap();
        assert_eq!(p.host, "9.9.9.9");
        assert_eq!(p.port, 8388);
    }

    #[test]
    fn пачка_считает_номера_строк_ошибок() {
        let raw = "hysteria2://pw@1.2.3.4:443#A\nмусор\nvless://u@1.2.3.4:443?security=reality&pbk=K&sni=a.com#B";
        let (ok, err) = parse_many(raw);
        assert_eq!(ok.len(), 2);
        assert_eq!(err.len(), 1);
        assert_eq!(err[0].0, 2);
    }
}
