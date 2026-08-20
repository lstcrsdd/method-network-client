//! Проба, её исход и накопленное состояние маршрута.

use crate::axis::Axis;
use crate::estimator::{Ewma, GilbertLoss, P2Quantile, Rfc3550Jitter, WilsonInterval};
use crate::ids::{NodeId, RouteId};
use crate::Instant;
use serde::{Deserialize, Serialize};

/// Почему замер признан недостоверным.
///
/// Это не отказ маршрута, а наша неспособность его измерить. Разница
/// принципиальна: недостоверный замер, засчитанный как отказ, наказывает
/// исправную ноду за нашу же ошибку. Эта путаница дважды стоила проекту
/// полдня отладки.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum InvalidCause {
    /// Маршрут по умолчанию ушёл в туннель: меряем сами себя.
    DefaultRouteThroughTunnel,
    /// Сменилась сеть — прежние значения не с чем сравнивать.
    NetworkChanged,
    /// Устройство спало: разрыв в монотонных часах.
    DeviceWasAsleep,
    /// Портал перехвата отвечает вместо цели.
    CaptivePortal,
    /// На машине поднят чужой туннель.
    ForeignTunnelDetected,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum ProbeOutcome {
    Ok { rtt_ms: f32 },
    /// Не ответила за отведённое время. Порог — свойство НАШЕГО решения, а
    /// не сети, поэтому он один на все маршруты и пишется рядом со значением.
    Timeout,
    HandshakeFailed,
    /// Соединение есть, но вышло не там, где ожидалось. Худший случай:
    /// туннель выглядит рабочим, а трафик идёт мимо.
    ExitMismatch { expected: NodeId, got: Option<NodeId> },
    DnsTampered,
    /// Замер выброшен. НЕ засчитывается как отказ.
    Discarded { cause: InvalidCause },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Probe {
    pub route: RouteId,
    pub at: Instant,
    pub outcome: ProbeOutcome,
}

/// Подтверждён ли выход через ожидаемый узел.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ExitVerdict {
    Confirmed,
    Mismatch,
    /// Проверить нечем — например, у чужого узла из подписки нет ключа для
    /// подтверждения. Это НЕ то же самое, что подтверждено.
    Unknown,
}

/// Накопленное состояние маршрута. Всё O(1) по памяти.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RouteHealth {
    pub route: RouteId,
    pub axis: Axis,

    pub rtt_p50: P2Quantile,
    pub rtt_p95: P2Quantile,
    pub jitter: Rfc3550Jitter,
    pub loss: GilbertLoss,
    pub handshake_ms: Ewma,
    /// `None` означает НЕ ИЗМЕРЕНО, а не ноль. Иначе выбор для загрузок
    /// делался бы по задержке под видом выбора по скорости.
    pub throughput_mbps: Option<Ewma>,

    pub sample_count: u32,
    pub consecutive_lost: u32,
    pub handshake_failed_streak: u32,
    pub last_sample_at: Option<Instant>,
    pub first_sample_at: Option<Instant>,
    pub verified_exit: ExitVerdict,
    pub dns_tampered: bool,
    pub ipv6_leak: bool,
}

impl RouteHealth {
    pub fn new(route: RouteId, axis: Axis) -> Self {
        Self {
            route,
            axis,
            rtt_p50: P2Quantile::new(0.50),
            rtt_p95: P2Quantile::new(0.95),
            jitter: Rfc3550Jitter::default(),
            loss: GilbertLoss::default(),
            handshake_ms: Ewma::new(120_000),
            throughput_mbps: None,
            sample_count: 0,
            consecutive_lost: 0,
            handshake_failed_streak: 0,
            last_sample_at: None,
            first_sample_at: None,
            verified_exit: ExitVerdict::Unknown,
            dns_tampered: false,
            ipv6_leak: false,
        }
    }

    /// Учесть пробу.
    ///
    /// Несостоявшаяся проба обновляет счётчик потерь как неуспех — иначе
    /// мёртвый маршрут навсегда замрёт с последней хорошей оценкой и будет
    /// выглядеть лучше живого. Исключение — выброшенный замер: он не
    /// говорит о маршруте ничего.
    pub fn observe(&mut self, p: &Probe) {
        let dt = match self.last_sample_at {
            Some(prev) => p.at.since(prev),
            None => 0,
        };

        match &p.outcome {
            ProbeOutcome::Discarded { .. } => return,
            ProbeOutcome::Ok { rtt_ms } => {
                let v = *rtt_ms;
                self.rtt_p50.update(v);
                self.rtt_p95.update(v);
                self.jitter.update(v);
                self.loss.update(false);
                self.consecutive_lost = 0;
                self.handshake_failed_streak = 0;
            }
            ProbeOutcome::Timeout => {
                self.loss.update(true);
                self.consecutive_lost += 1;
            }
            ProbeOutcome::HandshakeFailed => {
                self.loss.update(true);
                self.consecutive_lost += 1;
                self.handshake_failed_streak += 1;
            }
            ProbeOutcome::ExitMismatch { .. } => {
                self.verified_exit = ExitVerdict::Mismatch;
                self.loss.update(true);
                self.consecutive_lost += 1;
            }
            ProbeOutcome::DnsTampered => {
                self.dns_tampered = true;
            }
        }

        if matches!(p.outcome, ProbeOutcome::Ok { .. }) {
            self.verified_exit = match self.verified_exit {
                ExitVerdict::Mismatch => ExitVerdict::Mismatch,
                other => other,
            };
        }
        let _ = dt;
        self.sample_count += 1;
        self.last_sample_at = Some(p.at);
        if self.first_sample_at.is_none() {
            self.first_sample_at = Some(p.at);
        }
    }

    pub fn observe_handshake(&mut self, ms: f32, dt_ms: u64) {
        self.handshake_ms.update(ms, dt_ms);
    }

    pub fn observe_throughput(&mut self, mbps: f32, dt_ms: u64) {
        self.throughput_mbps
            .get_or_insert_with(|| Ewma::new(600_000))
            .update(mbps, dt_ms);
    }

    /// Смена сети обесценивает всё накопленное: задержки через другой канал
    /// с прежними несравнимы, а джиттер между ними — не джиттер.
    pub fn reset_for_new_network(&mut self) {
        let route = self.route.clone();
        let axis = self.axis;
        *self = RouteHealth::new(route, axis);
    }

    pub fn availability(&self) -> WilsonInterval {
        self.loss.wilson()
    }

    /// Разрыв между хвостом и серединой. Именно `p95 − p50`, а не
    /// `p95 − min`: оценка с опорой на минимум растёт с числом проб, то есть
    /// смещается ОПОРА, а не разброс, и маршруты с разным числом проб ею
    /// сравнивать нельзя.
    pub fn pdv_ms(&self) -> Option<f32> {
        Some((self.rtt_p95.get()? - self.rtt_p50.get()?).max(0.0))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn проба(at: u64, o: ProbeOutcome) -> Probe {
        Probe { route: RouteId::new("r"), at: Instant(at), outcome: o }
    }

    #[test]
    fn выброшенный_замер_не_наказывает_маршрут() {
        let mut h = RouteHealth::new(RouteId::new("r"), Axis::RealTls);
        for i in 0..5 {
            h.observe(&проба(i * 1000, ProbeOutcome::Ok { rtt_ms: 40.0 }));
        }
        let before = h.loss.total();
        h.observe(&проба(9000, ProbeOutcome::Discarded {
            cause: InvalidCause::DefaultRouteThroughTunnel,
        }));
        assert_eq!(h.loss.total(), before, "выброшенный замер попал в статистику потерь");
        assert_eq!(h.consecutive_lost, 0);
    }

    #[test]
    fn таймаут_считается_потерей_иначе_мертвый_маршрут_замрет_хорошим() {
        let mut h = RouteHealth::new(RouteId::new("r"), Axis::QuicUdp);
        h.observe(&проба(0, ProbeOutcome::Ok { rtt_ms: 30.0 }));
        for i in 1..4 {
            h.observe(&проба(i * 1000, ProbeOutcome::Timeout));
        }
        assert_eq!(h.consecutive_lost, 3);
        assert!(h.availability().lo < 0.5);
    }

    #[test]
    fn несовпадение_выхода_запоминается_навсегда_до_сброса() {
        let mut h = RouteHealth::new(RouteId::new("r"), Axis::RealTls);
        h.observe(&проба(0, ProbeOutcome::ExitMismatch {
            expected: NodeId::new("lt"),
            got: Some(NodeId::new("ru")),
        }));
        assert_eq!(h.verified_exit, ExitVerdict::Mismatch);
        for i in 1..10 {
            h.observe(&проба(i * 1000, ProbeOutcome::Ok { rtt_ms: 20.0 }));
        }
        assert_eq!(h.verified_exit, ExitVerdict::Mismatch, "успех стёр факт выхода мимо узла");
    }
}
