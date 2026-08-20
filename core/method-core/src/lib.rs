//! Ядро сетевого оркестратора Method.
//!
//! Ядро НЕ ходит в сеть, не читает часы и не знает про sing-box. Всё, что
//! оно делает, — превращает накопленные измерения и политику в решение.
//! Время и результаты проб подаются снаружи.
//!
//! Это не эстетика, а условие проверяемости: решения движка можно прогнать
//! на записанном логе измерений — без туннеля, без нод и без интернета, —
//! и сравнить число переключений с ожидаемым. Иначе гистерезис настраивается
//! вслепую.

pub mod axis;
pub mod damper;
pub mod decide;
pub mod estimator;
pub mod ids;
pub mod lane;
pub mod metrics;
pub mod replay;
pub mod route;
pub mod score;

pub use axis::{Axis, Exposure, ExposureSet};
pub use ids::{LaneId, ModeId, NodeId, RouteId};
pub use lane::{Hysteresis, Lane, OnEmpty, RouteRequirements, SwitchMode};
pub use metrics::{Probe, ProbeOutcome, RouteHealth};
pub use route::{Carries, Node, Route};
pub use score::{Score, SlaClass};

/// Момент времени в миллисекундах по монотонным часам платформы.
///
/// Именно монотонным: стенные часы прыгают при синхронизации и при выходе
/// из сна, а скачок назад превратил бы cooldown в вечный запрет.
#[derive(Copy, Clone, Debug, PartialEq, Eq, PartialOrd, Ord,
         serde::Serialize, serde::Deserialize)]
#[serde(transparent)]
pub struct Instant(pub u64);

impl Instant {
    /// Сколько прошло. Насыщается на нуле: если часы всё же прыгнули назад,
    /// лучше получить ноль, чем переполнение.
    pub fn since(self, earlier: Instant) -> u64 {
        self.0.saturating_sub(earlier.0)
    }

    pub fn plus_ms(self, ms: u64) -> Instant {
        Instant(self.0.saturating_add(ms))
    }
}
