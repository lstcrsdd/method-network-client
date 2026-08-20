//! Потоковые оценщики. Все O(1) по памяти: на телефоне держать историю проб
//! по десятку маршрутов нельзя, а решения принимаются каждые несколько секунд.

use serde::{Deserialize, Serialize};

/// Экспоненциальное среднее со сглаживанием ПО ВРЕМЕНИ, а не по числу
/// выборок.
///
/// Это не педантизм: активный маршрут опрашивается раз в 2 с, фоновые — раз
/// в 20 с. При фиксированном коэффициенте 1/8 период полураспада вышел бы
/// 26 с против 104 с, и оценки стали бы несравнимы между собой — ровно там,
/// где мы их сравниваем.
#[derive(Copy, Clone, Debug, Serialize, Deserialize)]
pub struct Ewma {
    value: f32,
    /// Период полураспада в миллисекундах.
    half_life_ms: u64,
    initialized: bool,
}

impl Ewma {
    pub fn new(half_life_ms: u64) -> Self {
        Self { value: 0.0, half_life_ms: half_life_ms.max(1), initialized: false }
    }

    pub fn update(&mut self, sample: f32, dt_ms: u64) {
        if !self.initialized {
            self.value = sample;
            self.initialized = true;
            return;
        }
        let alpha = 1.0 - (-(dt_ms as f32) / self.half_life_ms as f32 * std::f32::consts::LN_2).exp();
        let alpha = alpha.clamp(0.0, 1.0);
        self.value += alpha * (sample - self.value);
    }

    pub fn get(&self) -> Option<f32> {
        self.initialized.then_some(self.value)
    }

    pub fn get_or(&self, default: f32) -> f32 {
        self.get().unwrap_or(default)
    }

    pub fn is_initialized(&self) -> bool {
        self.initialized
    }

    pub fn reset(&mut self) {
        self.initialized = false;
        self.value = 0.0;
    }
}

/// Джиттер по RFC 3550 A.8: сглаженная средняя АБСОЛЮТНАЯ разность соседних
/// задержек.
///
/// Стандартное отклонение здесь хуже по существу: оно симметрично вокруг
/// среднего и ловит медленный дрейф задержки, которому интерактивный трафик
/// безразличен. `|ΔRTT|` ловит именно межпакетную дёрганость.
#[derive(Copy, Clone, Debug, Default, Serialize, Deserialize)]
pub struct Rfc3550Jitter {
    j: f32,
    prev: Option<f32>,
}

impl Rfc3550Jitter {
    pub fn update(&mut self, rtt_ms: f32) {
        if let Some(p) = self.prev {
            let d = (rtt_ms - p).abs();
            self.j += (d - self.j) / 16.0;
        }
        self.prev = Some(rtt_ms);
    }

    pub fn get(&self) -> f32 {
        self.j
    }

    /// Смена сети обесценивает историю: разность задержек через разные
    /// каналы джиттером не является.
    pub fn reset(&mut self) {
        self.j = 0.0;
        self.prev = None;
    }
}

/// Оценщик перцентилей P² (Jain & Chlamtac, 1985). Держит пять маркеров
/// вместо всей выборки.
#[derive(Copy, Clone, Debug, Serialize, Deserialize)]
pub struct P2Quantile {
    p: f32,
    q: [f32; 5],
    n: [f32; 5],
    np: [f32; 5],
    dn: [f32; 5],
    count: u32,
}

impl P2Quantile {
    pub fn new(p: f32) -> Self {
        let p = p.clamp(0.01, 0.99);
        Self {
            p,
            q: [0.0; 5],
            n: [1.0, 2.0, 3.0, 4.0, 5.0],
            np: [1.0, 1.0 + 2.0 * p, 1.0 + 4.0 * p, 3.0 + 2.0 * p, 5.0],
            dn: [0.0, p / 2.0, p, (1.0 + p) / 2.0, 1.0],
            count: 0,
        }
    }

    pub fn update(&mut self, x: f32) {
        if self.count < 5 {
            self.q[self.count as usize] = x;
            self.count += 1;
            if self.count == 5 {
                self.q.sort_by(|a, b| a.total_cmp(b));
            }
            return;
        }
        let k = if x < self.q[0] {
            self.q[0] = x;
            0
        } else if x >= self.q[4] {
            self.q[4] = x;
            3
        } else {
            (0..4).find(|&i| x < self.q[i + 1]).unwrap_or(3)
        };
        for i in (k + 1)..5 {
            self.n[i] += 1.0;
        }
        for i in 0..5 {
            self.np[i] += self.dn[i];
        }
        for i in 1..4 {
            let d = self.np[i] - self.n[i];
            let forward = self.n[i + 1] - self.n[i];
            let backward = self.n[i] - self.n[i - 1];
            if (d >= 1.0 && forward > 1.0) || (d <= -1.0 && backward < -1.0) {
                let d = d.signum();
                let qp = self.parabolic(i, d);
                self.q[i] = if self.q[i - 1] < qp && qp < self.q[i + 1] {
                    qp
                } else {
                    self.linear(i, d)
                };
                self.n[i] += d;
            }
        }
        self.count += 1;
    }

    fn parabolic(&self, i: usize, d: f32) -> f32 {
        let n = &self.n;
        let q = &self.q;
        q[i] + d / (n[i + 1] - n[i - 1])
            * ((n[i] - n[i - 1] + d) * (q[i + 1] - q[i]) / (n[i + 1] - n[i])
                + (n[i + 1] - n[i] - d) * (q[i] - q[i - 1]) / (n[i] - n[i - 1]))
    }

    fn linear(&self, i: usize, d: f32) -> f32 {
        let j = if d > 0.0 { i + 1 } else { i - 1 };
        self.q[i] + d * (self.q[j] - self.q[i]) / (self.n[j] - self.n[i])
    }

    /// `None`, пока не набралось пяти проб: перцентиль по трём выборкам —
    /// это не перцентиль.
    pub fn get(&self) -> Option<f32> {
        (self.count >= 5).then(|| self.q[2])
    }

    pub fn count(&self) -> u32 {
        self.count
    }
}

/// Доверительный интервал Уилсона для доли.
///
/// В оценку идёт НИЖНЯЯ граница — это делает свежий маршрут осторожнее
/// старого без отдельного механизма. Побочный эффект честный: восемь
/// успешных проб из восьми дают нижнюю границу всего 0.68, и заявить
/// высокую доступность раньше сотни проб нельзя.
#[derive(Copy, Clone, Debug, Serialize, Deserialize)]
pub struct WilsonInterval {
    pub lo: f32,
    pub hi: f32,
    pub n: u32,
}

impl WilsonInterval {
    pub fn compute(successes: u32, total: u32) -> Self {
        if total == 0 {
            return Self { lo: 0.0, hi: 1.0, n: 0 };
        }
        let n = total as f32;
        let p = successes as f32 / n;
        let z = 1.96_f32;
        let z2 = z * z;
        let denom = 1.0 + z2 / n;
        let center = (p + z2 / (2.0 * n)) / denom;
        let half = z * (p * (1.0 - p) / n + z2 / (4.0 * n * n)).sqrt() / denom;
        Self { lo: (center - half).max(0.0), hi: (center + half).min(1.0), n: total }
    }

    pub fn width(&self) -> f32 {
        self.hi - self.lo
    }
}

/// Модель Гилберта: потери приходят пачками, и пачка из десяти подряд
/// вреднее десяти разрозненных.
#[derive(Copy, Clone, Debug, Default, Serialize, Deserialize)]
pub struct GilbertLoss {
    n_good: u32,
    n_bad: u32,
    good_to_bad: u32,
    bad_to_good: u32,
    prev_lost: Option<bool>,
}

impl GilbertLoss {
    pub fn update(&mut self, lost: bool) {
        if let Some(prev) = self.prev_lost {
            match (prev, lost) {
                (false, true) => self.good_to_bad += 1,
                (true, false) => self.bad_to_good += 1,
                _ => {}
            }
        }
        if lost {
            self.n_bad += 1;
        } else {
            self.n_good += 1;
        }
        self.prev_lost = Some(lost);
    }

    /// Коэффициент пачечности. Ограничен двойкой: модель G.107 валидирована
    /// только до неё, и экстраполировать за границу валидации — значит
    /// выдавать выдумку за стандарт.
    pub fn burst_ratio(&self) -> f32 {
        if self.n_good < 5 || self.n_bad < 5 {
            return 1.0;
        }
        let p = self.good_to_bad as f32 / self.n_good as f32;
        let q = self.bad_to_good as f32 / self.n_bad as f32;
        if p + q <= 0.0 {
            return 1.0;
        }
        (1.0 / (p + q)).clamp(1.0, 2.0)
    }

    /// Достаточно ли данных, чтобы пачечности верить.
    pub fn is_confident(&self) -> bool {
        self.n_good >= 5 && self.n_bad >= 5
    }

    pub fn wilson(&self) -> WilsonInterval {
        WilsonInterval::compute(self.n_good, self.n_good + self.n_bad)
    }

    pub fn total(&self) -> u32 {
        self.n_good + self.n_bad
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ewma_сглаживает_по_времени_а_не_по_выборкам() {
        // Один и тот же ряд, поданный с разной частотой, обязан прийти к
        // одному значению за одно и то же ВРЕМЯ.
        let mut fast = Ewma::new(1000);
        let mut slow = Ewma::new(1000);
        fast.update(0.0, 0);
        slow.update(0.0, 0);
        for _ in 0..10 {
            fast.update(100.0, 100);
        }
        slow.update(100.0, 1000);
        let (f, s) = (fast.get().unwrap(), slow.get().unwrap());
        assert!((f - s).abs() < 6.0, "быстрый {f}, медленный {s}");
    }

    #[test]
    fn джиттер_видит_дерганость_а_не_дрейф() {
        // Медленный дрейф на 100 мс интерактиву безразличен...
        let mut drift = Rfc3550Jitter::default();
        for i in 0..100 {
            drift.update(50.0 + i as f32);
        }
        // ...а прыжки между двумя значениями — нет.
        let mut jump = Rfc3550Jitter::default();
        for i in 0..100 {
            jump.update(if i % 2 == 0 { 40.0 } else { 60.0 });
        }
        assert!(jump.get() > drift.get() * 5.0, "дрейф {}, прыжки {}", drift.get(), jump.get());
    }

    #[test]
    fn уилсон_наказывает_малую_выборку() {
        let eight = WilsonInterval::compute(8, 8);
        let hundred = WilsonInterval::compute(100, 100);
        assert!(eight.lo < 0.72, "восемь из восьми дали {}", eight.lo);
        assert!(hundred.lo > 0.95, "сто из ста дали {}", hundred.lo);
        assert!(eight.width() > hundred.width());
    }

    #[test]
    fn перцентиль_молчит_пока_мало_проб() {
        let mut q = P2Quantile::new(0.95);
        q.update(10.0);
        q.update(20.0);
        assert!(q.get().is_none());
        for i in 0..200 {
            q.update(i as f32);
        }
        let v = q.get().unwrap();
        assert!(v > 150.0 && v <= 200.0, "p95 вышел {v}");
    }

    #[test]
    fn пачечность_без_данных_равна_единице() {
        let mut g = GilbertLoss::default();
        for _ in 0..20 {
            g.update(false);
        }
        assert_eq!(g.burst_ratio(), 1.0);
        assert!(!g.is_confident());
    }
}
