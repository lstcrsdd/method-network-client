//! Сколько проб нужно, чтобы маршрут перешагнул порог участия в выборе.
//!
//! Вопрос не праздный: до порога движок честно отвечает «кандидатов нет», и
//! всё это время выбор остаётся за ядром. Знать цену прогрева надо в пробах
//! и в секундах, а не на глаз.
use method_core::metrics::{Probe, ProbeOutcome, RouteHealth};
use method_core::score::{self, SlaClass};
use method_core::{Axis, Instant, RouteId};

fn main() {
    const ШАГ: u64 = 6_000;
    for class in [SlaClass::Browse, SlaClass::Realtime, SlaClass::Bulk] {
        let mut h = RouteHealth::new(RouteId::new("ось"), Axis::FakeTlsH2);
        let mut порог: Option<u32> = None;
        for i in 0..200u32 {
            h.observe(&Probe {
                route: RouteId::new("ось"),
                at: Instant(i as u64 * ШАГ),
                outcome: ProbeOutcome::Ok { rtt_ms: 120.0 },
            });
            let s = score::score(&h, class, Instant(i as u64 * ШАГ), ШАГ);
            if порог.is_none() && s.confidence >= 0.5 {
                порог = Some(i + 1);
            }
        }
        match порог {
            Some(n) => println!(
                "  {:?}: порог участия на {} пробе — это {} с при шаге 6 с",
                class, n, n * 6
            ),
            None => println!("  {:?}: за 200 проб порог не взят", class),
        }
    }
}
