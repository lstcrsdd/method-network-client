//! Идентификаторы. Непрозрачные строки, а не индексы: план должен
//! хешироваться воспроизводимо, а порядок в массивах не гарантирован.

use serde::{Deserialize, Serialize};

macro_rules! id_type {
    ($name:ident, $doc:literal) => {
        #[doc = $doc]
        #[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
        #[serde(transparent)]
        pub struct $name(pub String);

        impl $name {
            pub fn new(s: impl Into<String>) -> Self {
                Self(s.into())
            }
            pub fn as_str(&self) -> &str {
                &self.0
            }
        }

        impl std::fmt::Display for $name {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str(&self.0)
            }
        }
    };
}

id_type!(NodeId, "Узел: `lt`, `us`, `fi`.");
id_type!(RouteId, "Маршрут: `lt.trojan.8443` — узел, транспорт, порт.");
id_type!(LaneId, "Полоса: `web`, `corp`, `call`, `bulk`, `lan`, `resolver`.");
id_type!(ModeId, "Режим: `Work`, `Privacy`, `Safe`.");
id_type!(TransportId, "Транспорт: `hysteria2`, `trojan`, `shadowsocks-2022`.");
