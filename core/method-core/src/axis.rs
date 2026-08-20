//! Ось обхода и экспозиция — два типа, на которых держится всё остальное.

use serde::{Deserialize, Serialize};

/// Ось обхода: независимый СПОСОБ пройти, а не протокол.
///
/// Протоколы на одной оси убивает одна и та же причина, поэтому запасной
/// маршрут на той же оси запасным не является. Переключение с Hysteria2 на
/// TUIC в сети, которая режет QUIC, не даёт ничего: оба стоят на `QuicUdp`.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Axis {
    /// Hysteria2, TUIC. Убивается фильтрацией QUIC.
    QuicUdp,
    /// Reality gRPC. Убивается распознаванием подделки Reality.
    FakeTlsH2,
    /// Reality Vision. Убивается тем же самым.
    FakeTlsTcp,
    /// Trojan. Сертификат подлинный, поэтому убить можно только блокировкой
    /// домена или адреса.
    RealTls,
    /// Shadowsocks-2022. Ни TLS, ни рукопожатия — просто шифрованный поток.
    RawStream,
    /// Прямой выход и блокировка. Оси не имеют.
    None,
}

impl Axis {
    pub const ALL: [Axis; 5] = [
        Axis::QuicUdp,
        Axis::FakeTlsH2,
        Axis::FakeTlsTcp,
        Axis::RealTls,
        Axis::RawStream,
    ];

    /// Человеческое имя для интерфейса и объяснений решений.
    pub fn human_ru(self) -> &'static str {
        match self {
            Axis::QuicUdp => "QUIC поверх UDP",
            Axis::FakeTlsH2 => "HTTP/2 в поддельном TLS",
            Axis::FakeTlsTcp => "голый TCP в поддельном TLS",
            Axis::RealTls => "настоящий TLS",
            Axis::RawStream => "шифрованный поток без рукопожатия",
            Axis::None => "без туннеля",
        }
    }
}

/// Куда выходит трафик. Это ТИП маршрута, проверяемый на компиляции, а не
/// свойство, о котором договорились.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum Exposure {
    /// Через наш узел.
    Tunnelled { node: crate::ids::NodeId },
    /// С настоящим адресом пользователя.
    Direct,
    /// Никуда.
    Blocked,
}

impl Exposure {
    pub fn bit(&self) -> u8 {
        match self {
            Exposure::Tunnelled { .. } => 1,
            Exposure::Direct => 2,
            Exposure::Blocked => 4,
        }
    }
}

/// Множество допустимых экспозиций полосы. Замыкание проверяется на
/// компиляции плана: ни один член селектора не смеет выходить за него.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(transparent)]
pub struct ExposureSet(pub u8);

impl ExposureSet {
    pub const TUNNELLED: ExposureSet = ExposureSet(1);
    pub const DIRECT: ExposureSet = ExposureSet(2);
    pub const BLOCKED: ExposureSet = ExposureSet(4);

    pub fn allows(self, e: &Exposure) -> bool {
        self.0 & e.bit() != 0
    }
    pub fn is_empty(self) -> bool {
        self.0 == 0
    }
    /// Разрешает ли выход открытым. Отдельный метод, потому что это условие
    /// встречается в проверках чаще всех прочих.
    pub fn permits_direct(self) -> bool {
        self.0 & 2 != 0
    }
    pub fn union(self, other: ExposureSet) -> ExposureSet {
        ExposureSet(self.0 | other.0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ids::NodeId;

    #[test]
    fn замкнутость_экспозиции() {
        let only_tunnel = ExposureSet::TUNNELLED;
        assert!(only_tunnel.allows(&Exposure::Tunnelled { node: NodeId::new("lt") }));
        assert!(!only_tunnel.allows(&Exposure::Direct));
        assert!(!only_tunnel.permits_direct());
    }

    #[test]
    fn пустое_множество_не_разрешает_ничего() {
        let none = ExposureSet(0);
        assert!(none.is_empty());
        assert!(!none.allows(&Exposure::Direct));
        assert!(!none.allows(&Exposure::Blocked));
    }
}
