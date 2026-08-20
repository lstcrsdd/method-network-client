//! Полоса — именованный класс трафика, за которым стоит подменяемый набор
//! маршрутов. Правило датаплейна называет полосу и никогда не называет
//! маршрут: иначе смена маршрута означала бы пересборку конфига.

use crate::axis::{Axis, ExposureSet};
use crate::ids::LaneId;
use crate::route::Route;
use crate::score::SlaClass;
use serde::{Deserialize, Serialize};

/// Что делать, когда живых кандидатов не осталось.
///
/// `Direct` здесь отсутствует НАМЕРЕННО и не может быть добавлен: это была
/// дыра, при которой падение всех маршрутов молча выпускало трафик открытым.
/// Разрешение на прямой выход требуется правилу, а `on_empty` — не правило,
/// поэтому проверка грантов его не покрывала.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum OnEmpty {
    /// Единственное безопасное умолчание.
    Block,
    /// Держать последний маршрут и громко сказать об этом человеку.
    HoldLast,
    /// Уйти в другую полосу. Компилятор проверяет, что у цели нет прямого
    /// выхода, цепочка ациклична и не длиннее двух.
    Fallback { lane: LaneId },
}

/// Рвать ли живые соединения при смене маршрута.
///
/// Переключение селектора само по себе их не рвёт — это асимметрия с
/// автопереизбранием ядра, и она нам на руку: выбор становится решением
/// политики, а не свойством конфига.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SwitchMode {
    /// Старые потоки дотекают по прежнему пути.
    Drain,
    /// Старые потоки обрываются. Нужно при ужесточении политики: иначе
    /// долгоживущее соединение, открытое в разрешительном режиме, переживёт
    /// переход и продолжит течь мимо новой политики.
    Cut,
}

/// Порог, время выдержки и остывание. Все три нужны вместе: убрать любой —
/// дребезг возвращается.
#[derive(Copy, Clone, Debug, Serialize, Deserialize)]
pub struct Hysteresis {
    /// Минимальный выигрыш в пунктах. Разрешается в `max(floor, 2σ)`, где
    /// σ — измеренный шум оценки: порог в 2 пункта бессмысленен при любом
    /// сглаживании.
    pub margin_floor: f32,
    /// Сколько замеров подряд условие обязано держаться.
    pub dwell: u8,
    /// Базовое остывание после переключения, миллисекунды. Растёт вдвое на
    /// каждое следующее переключение подряд.
    pub cooldown_ms: u64,
    /// Потолок остывания.
    pub cooldown_max_ms: u64,
}

impl Default for Hysteresis {
    fn default() -> Self {
        Self { margin_floor: 5.0, dwell: 3, cooldown_ms: 60_000, cooldown_max_ms: 1_800_000 }
    }
}

/// Требования полосы к маршруту. Проверяются до оценки: маршрут, не
/// подходящий по требованиям, не участвует в сравнении вовсе.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct RouteRequirements {
    pub axis_in: Option<Vec<Axis>>,
    pub axis_not_in: Vec<Axis>,
    pub exclude_country: Vec<String>,
    pub include_country: Option<Vec<String>>,
    pub require_udp: bool,
    pub require_v6: bool,
}

impl RouteRequirements {
    pub fn satisfied_by(&self, r: &Route, country: &str) -> bool {
        if let Some(ref allowed) = self.axis_in {
            if !allowed.contains(&r.axis) {
                return false;
            }
        }
        if self.axis_not_in.contains(&r.axis) {
            return false;
        }
        if self.exclude_country.iter().any(|c| c == country) {
            return false;
        }
        if let Some(ref only) = self.include_country {
            if !only.iter().any(|c| c == country) {
                return false;
            }
        }
        if self.require_udp && !r.carries.udp {
            return false;
        }
        if self.require_v6 && !r.carries.v6 {
            return false;
        }
        true
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Lane {
    pub id: LaneId,
    pub title: String,
    pub sla: SlaClass,
    /// Замыкание экспозиции. Ни один член селектора не смеет его нарушить.
    pub allow: ExposureSet,
    /// Обязательно для полосы, допускающей прямой выход. Печатается в
    /// разнице планов и в интерфейсе — «56 доменов» человек проматывает,
    /// а объяснение «зачем» заставляет остановиться.
    pub justification: Option<String>,
    pub need: RouteRequirements,
    /// Минимум независимых ОСЕЙ среди кандидатов. Меньше — план не
    /// собирается: полоса без запаса на другой оси не имеет запаса вообще.
    pub min_axes: u8,
    pub on_empty: OnEmpty,
    pub switch: SwitchMode,
    pub hysteresis: Hysteresis,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::axis::Exposure;
    use crate::ids::{NodeId, RouteId, TransportId};
    use crate::route::{Carries, HandshakeCost, Route};

    fn маршрут(axis: Axis, udp: bool) -> Route {
        Route {
            id: RouteId::new("x"),
            node: NodeId::new("lt"),
            transport: TransportId::new("t"),
            axis,
            exposure: Exposure::Tunnelled { node: NodeId::new("lt") },
            carries: Carries { tcp: true, udp, v4: true, v6: false },
            handshake_cost: HandshakeCost::Cheap,
        }
    }

    #[test]
    fn требование_udp_отсекает_tcp_маршрут() {
        let need = RouteRequirements { require_udp: true, ..Default::default() };
        assert!(need.satisfied_by(&маршрут(Axis::QuicUdp, true), "LT"));
        assert!(!need.satisfied_by(&маршрут(Axis::RealTls, false), "LT"));
    }

    #[test]
    fn ось_фильтруется_в_обе_стороны() {
        let only_real = RouteRequirements {
            axis_in: Some(vec![Axis::RealTls, Axis::RawStream]),
            ..Default::default()
        };
        assert!(only_real.satisfied_by(&маршрут(Axis::RealTls, true), "LT"));
        assert!(!only_real.satisfied_by(&маршрут(Axis::QuicUdp, true), "LT"));

        let no_quic = RouteRequirements { axis_not_in: vec![Axis::QuicUdp], ..Default::default() };
        assert!(!no_quic.satisfied_by(&маршрут(Axis::QuicUdp, true), "LT"));
        assert!(no_quic.satisfied_by(&маршрут(Axis::FakeTlsH2, true), "LT"));
    }
}
