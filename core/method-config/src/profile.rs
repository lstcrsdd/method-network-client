//! Профиль сервера — общая модель для всех клиентов.
//!
//! Формат кодирования повторяет тот, что уже лежит на дисках пользователей
//! macOS: тег `kind` плюс вложенный объект с параметрами протокола. Менять его
//! нельзя — иначе при первом же обновлении клиент не прочитает собственные
//! профили и человек останется без серверов.

use serde::{Deserialize, Serialize};

/// Транспортный протокол сервера.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum VpnProtocol {
    #[serde(rename = "hysteria2")]
    Hysteria2,
    #[serde(rename = "vless-reality")]
    VlessReality,
    #[serde(rename = "trojan")]
    Trojan,
    #[serde(rename = "shadowsocks")]
    Shadowsocks,
}

impl VpnProtocol {
    pub fn display_name(self) -> &'static str {
        match self {
            VpnProtocol::Hysteria2 => "Hysteria2",
            VpnProtocol::VlessReality => "VLESS + Reality",
            VpnProtocol::Trojan => "Trojan",
            VpnProtocol::Shadowsocks => "Shadowsocks",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Hysteria2 {
    pub password: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sni: Option<String>,
    #[serde(default)]
    pub allow_insecure: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub obfs_password: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub up_mbps: Option<i32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub down_mbps: Option<i32>,
    pub alpn: Vec<String>,
    /// Диапазон портов для прыжков. Из ссылок он ВЫРЕЗАЕТСЯ (см. `link.rs`) —
    /// за NAT прыжки рвут связь, замерено 2026-07-30. Поле осталось для старых
    /// каталогов, где оно ещё встречается.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub server_ports: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hop_interval: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct VlessReality {
    pub uuid: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub flow: Option<String>,
    pub sni: String,
    pub public_key: String,
    /// В Swift-модели поле называется `shortID` с двумя заглавными, и
    /// camelCase дал бы `shortId` — профиль на диске перестал бы читаться.
    #[serde(rename = "shortID")]
    pub short_id: String,
    pub fingerprint: String,
    /// Заполнено — транспорт gRPC, пусто — голый TCP с Vision. Это РАЗНЫЕ оси
    /// обхода: gRPC несёт HTTP/2-фрейминг, Vision — голый поток в поддельном
    /// TLS. Их ломает разное, поэтому нужны оба.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub grpc_service_name: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Trojan {
    pub password: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sni: Option<String>,
    #[serde(default)]
    pub allow_insecure: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fingerprint: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub grpc_service_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ws_path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ws_host: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Shadowsocks {
    /// Имя шифра передаётся ядру КАК ЕСТЬ: список поддерживаемых у sing-box
    /// свой, подменять его догадками нельзя.
    pub method: String,
    /// У семейства 2022 это base64-ключ фиксированной длины. Любая
    /// «нормализация» его ломает, поэтому пароль не трогаем никогда.
    pub password: String,
}

/// Параметры протокола. Тег `kind` — тот же, что в Swift-модели.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind")]
pub enum Parameters {
    #[serde(rename = "hysteria2")]
    Hysteria2 {
        #[serde(rename = "hysteria2")]
        params: Hysteria2,
    },
    #[serde(rename = "vless-reality")]
    VlessReality {
        #[serde(rename = "vlessReality")]
        params: VlessReality,
    },
    #[serde(rename = "trojan")]
    Trojan {
        #[serde(rename = "trojan")]
        params: Trojan,
    },
    #[serde(rename = "shadowsocks")]
    Shadowsocks {
        #[serde(rename = "shadowsocks")]
        params: Shadowsocks,
    },
}

impl Parameters {
    pub fn protocol(&self) -> VpnProtocol {
        match self {
            Parameters::Hysteria2 { .. } => VpnProtocol::Hysteria2,
            Parameters::VlessReality { .. } => VpnProtocol::VlessReality,
            Parameters::Trojan { .. } => VpnProtocol::Trojan,
            Parameters::Shadowsocks { .. } => VpnProtocol::Shadowsocks,
        }
    }
}

/// Профиль одного сервера.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerProfile {
    pub name: String,
    #[serde(rename = "protocol")]
    pub proto: VpnProtocol,
    pub host: String,
    pub port: u16,
    pub parameters: Parameters,
    /// Метка выделения, которую провайдер поставил в имя узла: «VIP», «⭐».
    /// `None` — обычный сервер. Отсутствующее поле при кодировании не пишется:
    /// старые каталоги про него не знают, и JSON для них не меняется.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub featured_label: Option<String>,
}

impl ServerProfile {
    pub fn is_featured(&self) -> bool {
        self.featured_label.is_some()
    }
}
