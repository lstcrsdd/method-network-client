//! Узел, маршрут, транспорт.
//!
//! Единица выбора — МАРШРУТ, а не узел. Замер 2026-08-20 с литовской ноды:
//! Финляндия по gRPC даёт медиану 65 мс при джиттере 19, она же по Reality
//! Vision — 209 мс при джиттере 125. Один и тот же сервер, тот же канал,
//! та же минута. Выбор «подключиться к Финляндии» не означает почти ничего.

use crate::axis::{Axis, Exposure};
use crate::ids::{NodeId, RouteId, TransportId};
use serde::{Deserialize, Serialize};

/// Что маршрут вообще способен нести. Проверяется до выбора: полосе с
/// требованием `require_udp` бессмысленно предлагать TCP-only маршрут.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Carries {
    pub tcp: bool,
    pub udp: bool,
    pub v4: bool,
    /// На FI пришлось принудить IPv4-egress, иначе выходной адрес не
    /// совпадал с ожидаемым и клиент браковал исправную ноду.
    pub v6: bool,
}

impl Default for Carries {
    fn default() -> Self {
        Self { tcp: true, udp: true, v4: true, v6: false }
    }
}

/// Насколько дорого маршруту установить соединение. Влияет на бюджет проб:
/// полное TLS-рукопожатие мерить каждые пять секунд нельзя.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum HandshakeCost {
    /// QUIC с 0-RTT.
    Cheap,
    /// Полное TLS-рукопожатие.
    Expensive,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Node {
    pub id: NodeId,
    pub country: String,
    pub city: String,
    /// Адреса выхода обеих семей. Нужны пакетному фильтру: он принимает
    /// только литералы, а Trojan живёт на доменах.
    pub egress_v4: Vec<String>,
    pub egress_v6: Vec<String>,
    pub has_ipv6_egress: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Route {
    pub id: RouteId,
    pub node: NodeId,
    pub transport: TransportId,
    pub axis: Axis,
    pub exposure: Exposure,
    pub carries: Carries,
    pub handshake_cost: HandshakeCost,
}

impl Route {
    /// Ключ группировки при поиске запасного пути: запас обязан отличаться
    /// и осью, и узлом — иначе он умрёт вместе с активным.
    pub fn diversity_key(&self) -> (Axis, &str) {
        (self.axis, self.node.as_str())
    }
}
