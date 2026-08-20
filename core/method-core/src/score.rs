//! Оценка качества маршрута под КЛАСС ТРАФИКА.
//!
//! Одного числа «качество маршрута» не существует. Узел с 35 мс и рваной
//! задержкой хорош для загрузки файла и плох для голоса; узел с 45 мс и
//! ровной — наоборот. Поэтому оценка всегда считается под класс, и модуль
//! возвращает не только число, но и то, ЧТО именно его ограничивает, и
//! насколько этому числу вообще можно верить.
//!
//! Порядок вычисления важен и обратному изменению не подлежит:
//!
//! 1. **Ворота** (5.3) — до всякой арифметики. Провал ворот даёт ноль и
//!    список причин, а не низкую оценку: даже геометрическое среднее
//!    оставляет маршруту с 12% потерь около семидесяти из ста для игр.
//!    Любая агрегация без ворот врёт.
//! 2. **Нормировка** каждой метрики кривой Хилла в отрезок 0..1.
//! 3. **Взвешенное геометрическое среднее** — не сумма.
//! 4. **Уверенность** и правило показа: числу с холодного старта верить
//!    нельзя, и честнее не показать его вовсе, чем показать красивое.
//!
//! Модуль чистый: ни часов, ни сети. Время подаётся аргументом `now`.

use crate::estimator::{Ewma, WilsonInterval};
use crate::metrics::{ExitVerdict, RouteHealth};
use crate::Instant;
use serde::{Deserialize, Serialize};

// ───────────────────────────── типы ─────────────────────────────

/// Класс требований к маршруту. Определяет и веса метрик, и пороги ворот:
/// 3% потерь безразличны загрузке торрента и убивают звонок.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SlaClass {
    /// Игры, звонки, удалённый терминал.
    Realtime,
    /// Обычный веб.
    Browse,
    /// Видео.
    Stream,
    /// Загрузки и синхронизация.
    Bulk,
    /// Всё, где цена ошибки — не неудобство, а раскрытие.
    Sensitive,
}

impl SlaClass {
    pub const ALL: [SlaClass; 5] = [
        SlaClass::Realtime,
        SlaClass::Browse,
        SlaClass::Stream,
        SlaClass::Bulk,
        SlaClass::Sensitive,
    ];
}

/// Ворота — некомпенсируемое условие. Провал означает дисквалификацию, а не
/// штраф: маршрут, подменяющий DNS, не становится приемлемым оттого, что у
/// него отличная задержка.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum GateId {
    /// Верхняя граница Уилсона по потерям выше порога класса.
    Loss,
    /// Хвост задержки (p95) выше порога класса.
    RttTail,
    /// Нижняя граница Уилсона по доступности ниже порога класса.
    Availability,
    /// Обнаружена подмена ответов DNS.
    DnsTampered,
    /// Выход через узел не подтверждён: либо подтверждение не совпало, либо
    /// подтвердить нечем, а класс этого не допускает.
    ExitUnverified,
    /// Рукопожатие не проходит подряд.
    HandshakeFailed,
    /// Утечка IPv6 мимо туннеля.
    Ipv6Leak,
}

/// Метрика. Нужна не только для весов: `Score::limiter` называет человеку
/// причину («ограничивает джиттер»), а не заставляет гадать по числу.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum MetricId {
    Rtt,
    Jitter,
    Loss,
    Throughput,
    Stability,
}

/// Итог оценки маршрута под класс.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Score {
    /// 0..100. При провале ворот — ровно 0.0.
    pub value: f32,
    /// Диапазон вместо числа при средней уверенности. `None` означает либо
    /// «уверенность высока, показывай число», либо «уверенности нет вовсе,
    /// не показывай ничего» — различаются по полю `confidence`, а
    /// исполняемое правило показа — в [`Score::display`].
    pub band: Option<(f32, f32)>,
    /// 0..1.
    pub confidence: f32,
    /// Что именно ограничивает: argmax по `w_i · (1 − q_i)`.
    pub limiter: MetricId,
    /// Непусто ⇒ маршрут дисквалифицирован.
    pub gates_failed: Vec<GateId>,
}

impl Score {
    pub fn is_disqualified(&self) -> bool {
        !self.gates_failed.is_empty()
    }

    /// Правило показа из 5.6, сделанное исполняемым, а не комментарием.
    ///
    /// Смещение измерено: при десяти пробах оценка систематически выше
    /// примерно на пять пунктов, чем при ста двадцати, потому что и p95, и
    /// разброс на малой выборке занижены. Поэтому при средней уверенности
    /// показывается диапазон вокруг ПЕССИМИСТИЧНОГО прочтения метрик, а при
    /// низкой — не показывается ничего.
    ///
    /// Дисквалификацию правило не описывает: её проверяют раньше и отдельно
    /// через [`Score::is_disqualified`] — это факт, а не число.
    pub fn display(&self) -> Display {
        if self.confidence < CONF_SHOW_BAND {
            Display::Measuring
        } else if self.confidence < CONF_SHOW_VALUE {
            match self.band {
                Some((lo, hi)) => Display::Band { lo, hi },
                None => Display::Measuring,
            }
        } else {
            Display::Value { value: self.value, limiter: self.limiter }
        }
    }
}

/// Что интерфейсу разрешено показать.
#[derive(Copy, Clone, Debug, PartialEq)]
pub enum Display {
    /// «Измеряем». Числа нет — и не будет, пока не наберётся статистика.
    Measuring,
    Band { lo: f32, hi: f32 },
    Value { value: f32, limiter: MetricId },
}

/// Веса метрик внутри класса. Сумма всегда равна единице — и после
/// перераспределения весов неизмеренных метрик тоже.
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct Weights {
    pub rtt: f32,
    pub jitter: f32,
    pub loss: f32,
    pub throughput: f32,
    pub stability: f32,
}

impl Weights {
    pub fn sum(&self) -> f32 {
        self.rtt + self.jitter + self.loss + self.throughput + self.stability
    }
}

/// Полный набор констант класса: веса, колени кривых, устойчивость к
/// потерям и пороги ворот.
#[derive(Copy, Clone, Debug)]
pub struct ClassProfile {
    pub weights: Weights,
    /// Задержка, на которой качество падает вдвое.
    pub x50_latency_ms: f32,
    /// Разброс задержки, на котором качество падает вдвое.
    pub x50_jitter_ms: f32,
    /// Устойчивость класса к потерям в форме G.107. Больше — терпимее.
    pub bpl: f32,
    /// Полоса, на которой класс удовлетворён наполовину. НАША константа.
    pub b50_mbps: f32,
    /// Ворота: верхняя граница Уилсона по доле потерь.
    pub gate_loss_hi: f32,
    /// Ворота: хвост задержки p95, мс.
    pub gate_rtt_p95_ms: f32,
    /// Ворота: нижняя граница Уилсона по доступности.
    pub gate_avail_lo: f32,
}

// ─────────────────────────── константы ───────────────────────────

/// Порог, ниже которого задержка не вредит вообще. G.107 не штрафует
/// задержку до 100 мс совсем; 20 мс — заведомо безопасная точка, одна на
/// все классы, чтобы кривые классов различались только коленом.
pub const X0_MS: f32 = 20.0;

/// Крутизна кривой для латентности.
///
/// Тройка взята не на глаз: у G.107 ущерб от задержки `Idd` равен нулю на
/// 100 мс, 0.16 на 150, 3.04 на 200, 14.76 на 300 и 24.07 на 400. То есть
/// первые пятьдесят миллисекунд сверх порога стоят шестнадцать сотых
/// пункта, а следующие двести — двадцать четыре пункта. Колено живёт
/// примерно в одной октаве по `log2(задержка/порог)`, и показатель 3 даёт
/// кривой ровно такую ширину перехода. Линейная нормировка на этом месте
/// врёт дважды: наказывает безвредные первые миллисекунды и не успевает
/// наказать вредные последние.
pub const K_LATENCY: f32 = 3.0;

/// Крутизна кривой для джиттера. Мягче, чем у задержки: буфер джиттера
/// частично прячет разброс, и обрыв качества там не такой резкий.
pub const K_JITTER: f32 = 2.0;

/// Крутизна кривой полосы. НАША константа: измерения формы «полоса против
/// удовлетворённости» у нас нет, а двойка даёт мягкое насыщение без
/// ступеньки.
pub const K_THROUGHPUT: f32 = 2.0;

/// Минимальное значение нормированной метрики под логарифмом. Ноль под
/// логарифмом даёт минус бесконечность, а нам нужен ноль на выходе, а не
/// NaN.
pub const Q_FLOOR: f32 = 1e-4;

/// Сколько подряд неудачных рукопожатий считать отказом. Та же двойка, что
/// в аварийном пути движка решений: одно неудачное рукопожатие бывает и на
/// исправном маршруте.
pub const HANDSHAKE_FAIL_STREAK: u32 = 2;

/// Расхождение быстрого и медленного средних, при котором доверие к оценке
/// обнуляется. Пятнадцать пунктов — три ширины margin: маршрут, чья оценка
/// гуляет на три порога переключения, не «хороший» и не «плохой», он
/// мигающий, и число для него бессмысленно. НАША константа.
pub const STAB_DIVERGENCE_FULL: f32 = 15.0;

/// Период полураспада быстрого среднего оценки. Совпадает с постоянной
/// сглаживания итоговой оценки в движке решений (α=1/8 при шаге 5 с).
pub const STAB_FAST_HALF_LIFE_MS: u64 = 40_000;

/// Период полураспада медленного среднего. На порядок больше быстрого:
/// два средних с близкими периодами расходятся только на шуме и ловили бы
/// не нестационарность, а его.
pub const STAB_SLOW_HALF_LIFE_MS: u64 = 400_000;

/// Ниже этой уверенности не показывается ничего.
pub const CONF_SHOW_BAND: f32 = 0.25;
/// От этой уверенности показывается число, а не диапазон.
pub const CONF_SHOW_VALUE: f32 = 0.60;

/// Число проб, на котором `C_n` выходит на единицу.
pub const CONF_FULL_SAMPLES: f32 = 30.0;
/// Ширина интервала Уилсона, при которой `C_stat` обнуляется.
pub const CONF_WILSON_FULL_WIDTH: f32 = 0.20;

/// Квадрат z для 95%. Тот же, что в [`WilsonInterval`]; продублирован,
/// потому что здесь он нужен для обратной задачи — сколько проб надо,
/// чтобы ворота вообще могли быть пройдены.
const Z2: f32 = 1.96 * 1.96;

impl SlaClass {
    /// Таблица весов и порогов (5.4 и 5.3 проектного документа).
    ///
    /// **О происхождении чисел честно.** Литература обосновывает
    /// УПОРЯДОЧЕННОСТЬ и примерное СООТНОШЕНИЕ весов внутри строки, а не
    /// третий знак после запятой. Игры — Claypool & Claypool (CACM 2006:
    /// игрок замечает задержку от 85 мс и терпит примерно до 600) и ITU-T
    /// G.1072. Браузер — Belshe (2010): каждые 20 мс RTT дают 7–15%
    /// времени загрузки, а рост канала с 5 до 10 Мбит/с — всего 5%; отсюда
    /// вес задержки примерно впятеро выше веса полосы. Стриминг —
    /// Krishnan & Sitaraman (IMC 2012, 23 млн просмотров): ребуферинг
    /// длиной в 1% ролика отнимает около 5% просмотра.
    ///
    /// Строка `Sensitive` внешнего источника НЕ имеет — это наше решение:
    /// там доминируют стабильность и ворота, потому что цена ошибки не в
    /// комфорте, а в раскрытии.
    ///
    /// `b50_mbps` тоже наша: измерения формы «полоса против
    /// удовлетворённости» у нас нет, порядок величин взят из требований
    /// кодеков и типичных битрейтов.
    pub fn profile(self) -> ClassProfile {
        match self {
            SlaClass::Realtime => ClassProfile {
                weights: Weights {
                    rtt: 0.34,
                    jitter: 0.24,
                    loss: 0.24,
                    throughput: 0.04,
                    stability: 0.14,
                },
                x50_latency_ms: 80.0,
                x50_jitter_ms: 30.0,
                bpl: 8.0,
                b50_mbps: 3.0,
                gate_loss_hi: 0.02,
                gate_rtt_p95_ms: 250.0,
                gate_avail_lo: 0.90,
            },
            SlaClass::Browse => ClassProfile {
                weights: Weights {
                    rtt: 0.34,
                    jitter: 0.06,
                    loss: 0.16,
                    throughput: 0.14,
                    stability: 0.30,
                },
                x50_latency_ms: 200.0,
                x50_jitter_ms: 80.0,
                bpl: 20.0,
                b50_mbps: 10.0,
                gate_loss_hi: 0.05,
                gate_rtt_p95_ms: 800.0,
                gate_avail_lo: 0.90,
            },
            SlaClass::Stream => ClassProfile {
                weights: Weights {
                    rtt: 0.05,
                    jitter: 0.10,
                    loss: 0.20,
                    throughput: 0.35,
                    stability: 0.30,
                },
                x50_latency_ms: 400.0,
                x50_jitter_ms: 120.0,
                bpl: 25.0,
                b50_mbps: 20.0,
                gate_loss_hi: 0.05,
                gate_rtt_p95_ms: 1200.0,
                gate_avail_lo: 0.90,
            },
            SlaClass::Bulk => ClassProfile {
                weights: Weights {
                    rtt: 0.02,
                    jitter: 0.03,
                    loss: 0.15,
                    throughput: 0.55,
                    stability: 0.25,
                },
                x50_latency_ms: 800.0,
                x50_jitter_ms: 400.0,
                bpl: 40.0,
                b50_mbps: 50.0,
                gate_loss_hi: 0.10,
                gate_rtt_p95_ms: 3000.0,
                gate_avail_lo: 0.85,
            },
            SlaClass::Sensitive => ClassProfile {
                weights: Weights {
                    rtt: 0.10,
                    jitter: 0.05,
                    loss: 0.15,
                    throughput: 0.10,
                    stability: 0.60,
                },
                x50_latency_ms: 800.0,
                x50_jitter_ms: 200.0,
                bpl: 20.0,
                b50_mbps: 10.0,
                gate_loss_hi: 0.05,
                gate_rtt_p95_ms: 800.0,
                gate_avail_lo: 0.95,
            },
        }
    }
}

// ──────────────────────── нормировка метрик ────────────────────────

/// Кривая Хилла для «меньше — лучше».
///
/// ```text
/// q(x) = 1                                  при x ≤ x0
/// q(x) = 1 / (1 + ((x − x0)/(x50 − x0))^k)  иначе
/// ```
///
/// Связь ощущаемого качества с сетевыми показателями экспоненциальна
/// (гипотеза IQX), а не линейна: до колена ущерба почти нет, за коленом он
/// набегает лавиной. Линейная нормировка ставит середину шкалы в середину
/// диапазона значений, а не в середину ущерба, — и тогда маршрут, уже
/// негодный для голоса, получает добротные полшкалы.
pub fn hill(x: f32, x0: f32, x50: f32, k: f32) -> f32 {
    if !x.is_finite() || x <= x0 {
        return 1.0;
    }
    let span = (x50 - x0).max(f32::EPSILON);
    let t = (x - x0) / span;
    (1.0 / (1.0 + t.powf(k))).clamp(0.0, 1.0)
}

/// Кривая насыщения для «больше — лучше» (полоса пропускания).
/// `q(b50) = 0.5`, растёт монотонно, единицы не достигает никогда —
/// бесконечной полосы не бывает.
pub fn hill_saturating(x: f32, x50: f32, k: f32) -> f32 {
    if !x.is_finite() || x <= 0.0 {
        return 0.0;
    }
    let x50 = x50.max(f32::EPSILON);
    (1.0 / (1.0 + (x50 / x).powf(k))).clamp(0.0, 1.0)
}

/// Качество по потерям в форме `Ie-eff` из ITU-T G.107 (формула 3-29) при
/// нулевом кодековом ущербе:
///
/// ```text
/// Ie_eff = 95 · Ppl / (Ppl/BurstR + Bpl) ,   q = 1 − Ie_eff/95
/// ```
///
/// **Важная оговорка, которую нельзя потерять.** `Bpl` в G.113 — это
/// устойчивость КОДЕКА к потерям. Мы используем его как устойчивость
/// КЛАССА ТРАФИКА, и это аналогия, а НЕ стандарт. Если через год кто-то
/// сошлётся на G.113 как на источник значения 40 для загрузок — он
/// ошибётся: сорок здесь наше.
///
/// Пачечность входит именно так, как в стандарте: десять потерь подряд
/// вреднее десяти разрозненных, потому что подряд теряется смысл, а
/// вразброс — только биты.
pub fn loss_quality(ppl_fraction: f32, burst_ratio: f32, bpl: f32) -> f32 {
    let ppl = (ppl_fraction.clamp(0.0, 1.0)) * 100.0;
    if ppl <= 0.0 {
        return 1.0;
    }
    let br = burst_ratio.clamp(1.0, 2.0);
    let denom = ppl / br + bpl.max(f32::EPSILON);
    (1.0 - ppl / denom).clamp(0.0, 1.0)
}

// ───────────────────── состояние для стабильности ─────────────────────

/// Два экспоненциальных средних оценки с разными периодами полураспада.
///
/// Стабильность — единственная метрика, которую нельзя вычислить из
/// [`RouteHealth`]: она про то, как оценка ведёт себя ВО ВРЕМЕНИ, а
/// `RouteHealth` хранит только текущее состояние. Ни число проб, ни их
/// возраст мигающего маршрута не выдают: он всё время «свежий и
/// измеренный», просто каждый раз разный.
///
/// Состояние живёт здесь, а не в `RouteHealth`, потому что кормить его
/// нужно уже посчитанной оценкой — то есть ПОСЛЕ модуля метрик. Владеть
/// трекером должен тот же слой, что владеет `RouteHealth` (снимок здоровья
/// в движке решений), по одному на пару «маршрут × класс»: оценка считается
/// под класс, и расхождение под класс тоже.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct StabilityTracker {
    fast: Ewma,
    slow: Ewma,
    /// Момент последнего кормления в тех же миллисекундах, что и `Instant`.
    /// Хранится числом, а не `Instant`, чтобы трекер сохранялся между
    /// запусками (I19) независимо от того, есть ли у `Instant` сериализация.
    last_at_ms: Option<u64>,
}

impl Default for StabilityTracker {
    fn default() -> Self {
        Self::new()
    }
}

impl StabilityTracker {
    pub fn new() -> Self {
        Self {
            fast: Ewma::new(STAB_FAST_HALF_LIFE_MS),
            slow: Ewma::new(STAB_SLOW_HALF_LIFE_MS),
            last_at_ms: None,
        }
    }

    /// Скормить базовую оценку — ту, что посчитана БЕЗ вклада стабильности
    /// ([`base_score`]). Иначе метрика зависела бы от самой себя, и
    /// расхождение средних говорило бы о собственной обратной связи, а не о
    /// маршруте.
    pub fn observe(&mut self, base_score: f32, now: Instant) {
        let dt = self.last_at_ms.map(|p| now.0.saturating_sub(p)).unwrap_or(0);
        self.fast.update(base_score, dt);
        self.slow.update(base_score, dt);
        self.last_at_ms = Some(now.0);
    }

    /// Расхождение средних в пунктах. `None`, пока средние не разошлись по
    /// времени настолько, чтобы их разность что-то значила.
    pub fn divergence(&self) -> Option<f32> {
        let (f, s) = (self.fast.get()?, self.slow.get()?);
        Some((f - s).abs())
    }

    /// Сглаженная оценка — то, что движок решений сравнивает с порогом.
    pub fn smoothed(&self) -> Option<f32> {
        self.fast.get()
    }

    /// `C_stab = 1 − min(1, |быстрое − медленное| / 15)`.
    pub fn c_stab(&self) -> Option<f32> {
        self.divergence()
            .map(|d| (1.0 - (d / STAB_DIVERGENCE_FULL).min(1.0)).clamp(0.0, 1.0))
    }

    pub fn reset(&mut self) {
        *self = Self::new();
    }
}

// ──────────────────────────── ворота ────────────────────────────

/// При скольких пробах ворота по доле вообще МОГУТ быть пройдены.
///
/// Верхняя граница Уилсона при нуле неудач равна `z²/(n + z²)`: при десяти
/// пробах это 27%, и ворота «потери выше 2%» провалит идеальный маршрут.
/// Такие ворота дисквалифицируют за незнание, а не за качество, — а это
/// ровно та ошибка, из-за которой клиент когда-то браковал исправную ноду.
/// Поэтому ворота, недостижимые при текущем числе проб, не применяются
/// вовсе; маршрут в это время всё равно не участвует в выборе, потому что
/// не проходит порог уверенности.
fn n_min_for_loss_gate(threshold: f32) -> u32 {
    if threshold <= 0.0 {
        return u32::MAX;
    }
    (Z2 * (1.0 - threshold) / threshold).ceil() as u32
}

/// Симметрично: нижняя граница при отсутствии неудач равна `n/(n + z²)`.
fn n_min_for_availability_gate(threshold: f32) -> u32 {
    if threshold >= 1.0 {
        return u32::MAX;
    }
    (Z2 * threshold / (1.0 - threshold)).ceil() as u32
}

/// Проверка ворот. Порядок в списке — по убыванию тяжести: первым идёт то,
/// что человеку показывают как причину.
fn gates(h: &RouteHealth, sla: SlaClass, p: &ClassProfile) -> Vec<GateId> {
    let mut failed = Vec::new();

    // Факты. Не зависят ни от числа проб, ни от арифметики: подмена DNS
    // остаётся подменой и на первой пробе.
    if h.dns_tampered {
        failed.push(GateId::DnsTampered);
    }
    match h.verified_exit {
        ExitVerdict::Mismatch => failed.push(GateId::ExitUnverified),
        // «Подтвердить нечем» — не то же самое, что «подтверждено». Для
        // обычных полос это терпимо (у чужого узла из подписки нет ключа),
        // для Sensitive — нет: там непроверяемый выход и есть та самая
        // цена ошибки.
        ExitVerdict::Unknown if sla == SlaClass::Sensitive => {
            failed.push(GateId::ExitUnverified)
        }
        _ => {}
    }
    if h.ipv6_leak {
        failed.push(GateId::Ipv6Leak);
    }
    if h.handshake_failed_streak >= HANDSHAKE_FAIL_STREAK {
        failed.push(GateId::HandshakeFailed);
    }

    // Статистика.
    let avail = h.availability();
    // Интервал Уилсона симметричен относительно замены p ↔ 1−p, поэтому
    // верхняя граница доли потерь — это единица минус нижняя граница
    // доступности. Отдельного счётчика потерь заводить не нужно.
    let loss_hi = 1.0 - avail.lo;

    if avail.n >= n_min_for_loss_gate(p.gate_loss_hi) && loss_hi > p.gate_loss_hi {
        failed.push(GateId::Loss);
    }
    if avail.n >= n_min_for_availability_gate(p.gate_avail_lo) && avail.lo < p.gate_avail_lo {
        failed.push(GateId::Availability);
    }
    if let Some(p95) = h.rtt_p95.get() {
        if p95 > p.gate_rtt_p95_ms {
            failed.push(GateId::RttTail);
        }
    }

    failed
}

/// Чем объяснять ноль, если ворота провалены.
fn limiter_for_gate(g: GateId) -> MetricId {
    match g {
        GateId::Loss | GateId::Availability => MetricId::Loss,
        GateId::RttTail => MetricId::Rtt,
        GateId::DnsTampered
        | GateId::ExitUnverified
        | GateId::HandshakeFailed
        | GateId::Ipv6Leak => MetricId::Stability,
    }
}

// ──────────────────────────── агрегация ────────────────────────────

/// Нормированные метрики. `None` означает НЕ ИЗМЕРЕНО — и это не то же
/// самое, что «плохо».
#[derive(Copy, Clone, Debug)]
struct Qualities {
    rtt: f32,
    jitter: f32,
    loss: f32,
    throughput: Option<f32>,
    stability: Option<f32>,
}

/// Веса после исключения неизмеренных метрик.
///
/// Вес неизмеренной метрики перераспределяется на остальные ПРОПОРЦИОНАЛЬНО,
/// а не приравнивается к нулю и не отдаётся кому-то одному. Подставлять
/// вместо неизмеренной полосы ноль нельзя категорически: выбор для загрузок
/// тогда делается по задержке под видом выбора по скорости. Считать её
/// единицей — тоже нельзя: в геометрическом среднем единица бесплатна, и
/// неизмеренный маршрут молча обошёл бы измеренный.
pub fn effective_weights(sla: SlaClass, has_throughput: bool, has_stability: bool) -> Weights {
    let mut w = sla.profile().weights;
    if !has_throughput {
        w.throughput = 0.0;
    }
    if !has_stability {
        w.stability = 0.0;
    }
    let sum = w.sum();
    if sum > 0.0 {
        let k = 1.0 / sum;
        w.rtt *= k;
        w.jitter *= k;
        w.loss *= k;
        w.throughput *= k;
        w.stability *= k;
    }
    w
}

/// Взвешенное ГЕОМЕТРИЧЕСКОЕ среднее:
/// `S = 100 · exp( Σ w_i · ln clamp(q_i, 1e-4, 1) )`.
///
/// Не сумма. Аддитивная агрегация предполагает полную ВЗАИМОЗАМЕНЯЕМОСТЬ
/// метрик: по ней маршрут с идеальной задержкой и разорванным в клочья
/// джиттером выходит приличным, потому что хорошая метрика компенсирует
/// плохую вплоть до нуля. В сети так не бывает — ни один канал не
/// становится пригодным для звонка оттого, что он быстрый. Геометрическое
/// среднее компенсацию ограничивает: одна метрика у нуля тянет к нулю всё
/// произведение. Оно же согласовано с IQX: если ощущаемое качество
/// экспоненциально по сетевому, то в логарифмах вклады складываются, то
/// есть сумма живёт под логарифмом, а не над ним.
fn aggregate(q: &Qualities, w: &Weights) -> (f32, MetricId) {
    let terms: [(MetricId, f32, Option<f32>); 5] = [
        (MetricId::Rtt, w.rtt, Some(q.rtt)),
        (MetricId::Jitter, w.jitter, Some(q.jitter)),
        (MetricId::Loss, w.loss, Some(q.loss)),
        (MetricId::Throughput, w.throughput, q.throughput),
        (MetricId::Stability, w.stability, q.stability),
    ];

    let mut ln_sum = 0.0f32;
    let mut limiter = MetricId::Rtt;
    let mut worst = f32::NEG_INFINITY;
    for (id, wi, qi) in terms {
        let Some(qi) = qi else { continue };
        if wi <= 0.0 {
            continue;
        }
        ln_sum += wi * qi.clamp(Q_FLOOR, 1.0).ln();
        // Ограничитель — argmax по w_i·(1 − q_i): не самая плохая метрика,
        // а та, что отняла больше всего очков. Неизмеренная метрика
        // ограничителем быть не может: назвать причиной то, чего мы не
        // мерили, — значит соврать.
        let damage = wi * (1.0 - qi.clamp(0.0, 1.0));
        if damage > worst {
            worst = damage;
            limiter = id;
        }
    }
    ((100.0 * ln_sum.exp()).clamp(0.0, 100.0), limiter)
}

// ─────────────────── сбор нормированных метрик ───────────────────

/// Точечная оценка доли по интервалу Уилсона.
///
/// `GilbertLoss` не отдаёт сырые счётчики наружу, а менять его нельзя,
/// поэтому долю восстанавливаем из центра интервала — это точная алгебра,
/// а не приближение: `center = (p + z²/2n)/(1 + z²/n)`.
fn point_estimate(w: &WilsonInterval) -> Option<f32> {
    if w.n == 0 {
        return None;
    }
    let n = w.n as f32;
    let center = (w.lo + w.hi) / 2.0;
    Some((center * (1.0 + Z2 / n) - Z2 / (2.0 * n)).clamp(0.0, 1.0))
}

/// Качество стабильности. Измеримо только при наличии истории оценки
/// (см. [`StabilityTracker`]) — иначе `None`, и вес уходит другим метрикам.
///
/// Пачечность потерь и свежая серия неудач домножаются в обоих случаях:
/// это прямая улика нестационарности прямо сейчас, доступная без истории.
fn stability_quality(h: &RouteHealth, stab: Option<&StabilityTracker>) -> Option<f32> {
    let base = stab.and_then(|t| t.c_stab())?;
    Some((base * instability_penalty(h)).clamp(0.0, 1.0))
}

/// Множитель 0..1 по уликам нестационарности, не требующим истории.
fn instability_penalty(h: &RouteHealth) -> f32 {
    // Пачечность считается только когда данных хватает (иначе она равна
    // единице по построению) — сама модель Гилберта об этом заботится.
    let burst = 1.0 - 0.5 * (h.loss.burst_ratio() - 1.0).clamp(0.0, 1.0);
    // Три потери подряд — это уже аварийный факт для движка решений;
    // здесь та же тройка задаёт шкалу.
    let streak = 1.0 - (h.consecutive_lost as f32 / 3.0).min(1.0);
    (burst * streak).clamp(0.0, 1.0)
}

/// Собрать нормированные метрики. `pessimistic` включает прочтение по
/// худшей границе — оно нужно для центра диапазона при средней уверенности.
fn qualities(
    h: &RouteHealth,
    p: &ClassProfile,
    stab: Option<&StabilityTracker>,
    pessimistic: bool,
) -> Option<Qualities> {
    let p50 = h.rtt_p50.get()?;
    let avail = h.availability();

    // Пессимистичное прочтение латентности — хвост вместо середины.
    // Настоящего доверительного интервала по задержке мы не держим (это
    // стоило бы памяти на маршрут), а p95 — честная верхняя граница по тем
    // данным, что есть.
    let lat = if pessimistic { h.rtt_p95.get().unwrap_or(p50) } else { p50 };

    // Разброс — p95 − p50, а НЕ p95 − min. Оценка с опорой на минимум
    // растёт с числом проб (18.6 мс при десяти пробах против 27.0 при
    // трёхстах на одном и том же распределении): смещается опора, а не
    // разброс, и маршруты с разным числом проб ею сравнивать нельзя.
    let pdv = h.pdv_ms().unwrap_or(0.0);

    let ppl = match point_estimate(&avail) {
        // Пессимизм по потерям — верхняя граница Уилсона.
        Some(_) if pessimistic => 1.0 - avail.lo,
        Some(ok) => 1.0 - ok,
        None => 0.0,
    };

    Some(Qualities {
        rtt: hill(lat, X0_MS, p.x50_latency_ms, K_LATENCY),
        jitter: hill(pdv, X0_MS, p.x50_jitter_ms, K_JITTER),
        loss: loss_quality(ppl, h.loss.burst_ratio(), p.bpl),
        throughput: h
            .throughput_mbps
            .as_ref()
            .and_then(|e| e.get())
            .map(|b| hill_saturating(b, p.b50_mbps, K_THROUGHPUT)),
        stability: stability_quality(h, stab),
    })
}

// ──────────────────────────── уверенность ────────────────────────────

/// Уверенность в оценке: `C = C_n · C_age · C_stat · C_stab`.
///
/// ```text
/// C_n    = min(1, n/30)
/// C_age  = 0.5^(возраст / (3 · интервал проб))
/// C_stat = 1 − min(1, ширина интервала Уилсона / 0.20)
/// C_stab = 1 − min(1, |быстрое EWMA − медленное| / 15)
/// ```
///
/// Произведение, а не среднее: любой из четырёх поводов не верить числу
/// достаточен сам по себе, и компенсировать «данные протухли» тем, что «их
/// зато много», нельзя.
///
/// При шаге проб 5 с это даёт 0.00 на восьми пробах, 0.12 на двадцати,
/// 0.39 на тридцати, 0.64 на шестидесяти и 0.77 на ста двадцати: маршрут
/// заслуживает доверия не раньше двух с половиной минут наблюдения.
/// Медленно — но альтернатива не «быстро», а «уверенно и неправильно».
///
/// Функция намеренно НЕ знает класса трафика: это уверенность в ИЗМЕРЕНИЯХ.
/// Поправка на метрики, не измеренные для конкретного класса, добавляется
/// в [`score`] и видна в `Score::confidence`.
pub fn confidence(h: &RouteHealth, now: Instant, probe_interval_ms: u64) -> f32 {
    confidence_with(h, now, probe_interval_ms, None)
}

/// То же, но с историей оценки, если она есть. Без трекера множитель
/// `C_stab` берётся по уликам нестационарности, доступным без истории
/// (пачечность потерь и свежая серия неудач).
pub fn confidence_with(
    h: &RouteHealth,
    now: Instant,
    probe_interval_ms: u64,
    stab: Option<&StabilityTracker>,
) -> f32 {
    let Some(last) = h.last_sample_at else { return 0.0 };

    let c_n = (h.sample_count as f32 / CONF_FULL_SAMPLES).min(1.0);

    let interval = probe_interval_ms.max(1) as f32;
    let age = now.since(last) as f32;
    let c_age = 0.5f32.powf(age / (3.0 * interval));

    let width = h.availability().width();
    let c_stat = 1.0 - (width / CONF_WILSON_FULL_WIDTH).min(1.0);

    let c_stab = stab.and_then(|t| t.c_stab()).unwrap_or(1.0) * instability_penalty(h);

    (c_n * c_age * c_stat * c_stab).clamp(0.0, 1.0)
}

/// Насколько снижается доверие оттого, что часть веса класса приходится на
/// НЕИЗМЕРЕННЫЕ метрики.
///
/// Половина недостающего веса, а не весь: обнулять нельзя. У загрузок вес
/// полосы 0.55, и полный штраф не дал бы классу `Bulk` перешагнуть порог
/// участия в выборе (0.5) НИКОГДА, пока не сделан активный замер скорости,
/// — то есть осторожность превратилась бы в отказ обслуживать. НАША
/// константа.
fn missing_metric_confidence(sla: SlaClass, has_throughput: bool, has_stability: bool) -> f32 {
    let w = sla.profile().weights;
    let mut missing = 0.0;
    if !has_throughput {
        missing += w.throughput;
    }
    if !has_stability {
        missing += w.stability;
    }
    (1.0 - missing / 2.0).clamp(0.0, 1.0)
}

// ──────────────────────────── вход модуля ────────────────────────────

/// Оценка маршрута под класс. Чистая функция от накопленного здоровья.
pub fn score(h: &RouteHealth, sla: SlaClass, now: Instant, probe_interval_ms: u64) -> Score {
    score_with(h, sla, now, probe_interval_ms, None)
}

/// То же, но с историей оценки: тогда стабильность становится измеренной
/// метрикой, а не перераспределённым весом.
pub fn score_with(
    h: &RouteHealth,
    sla: SlaClass,
    now: Instant,
    probe_interval_ms: u64,
    stab: Option<&StabilityTracker>,
) -> Score {
    let p = sla.profile();

    // 1. Ворота — раньше всякой арифметики.
    let failed = gates(h, sla, &p);
    if !failed.is_empty() {
        return Score {
            value: 0.0,
            band: None,
            confidence: confidence_with(h, now, probe_interval_ms, stab),
            limiter: limiter_for_gate(failed[0]),
            gates_failed: failed,
        };
    }

    // 2. Нормировка.
    let Some(q) = qualities(h, &p, stab, false) else {
        // Мерить ещё нечего. Это НЕ ноль качества и НЕ дисквалификация:
        // просто числа пока нет. Уверенность обнуляется явно — доверять
        // числу, которого мы не посчитали, не к чему.
        return Score {
            value: 0.0,
            band: None,
            confidence: 0.0,
            limiter: MetricId::Rtt,
            gates_failed: Vec::new(),
        };
    };
    let w = effective_weights(sla, q.throughput.is_some(), q.stability.is_some());

    // 3. Агрегация.
    let (value, limiter) = aggregate(&q, &w);

    // 4. Уверенность и правило показа.
    let conf = confidence_with(h, now, probe_interval_ms, stab)
        * missing_metric_confidence(sla, q.throughput.is_some(), q.stability.is_some());

    let band = if (CONF_SHOW_BAND..CONF_SHOW_VALUE).contains(&conf) {
        // Центр диапазона — по пессимистичному прочтению, потому что
        // смещение измерено и направлено: малая выборка занижает и хвост, и
        // разброс, то есть врёт в приятную сторону.
        let center = qualities(h, &p, stab, true)
            .map(|pq| aggregate(&pq, &w).0)
            .unwrap_or(value);
        let half = 12.0 * (1.0 - conf) + 1.0;
        Some(((center - half).max(0.0), (center + half).min(100.0)))
    } else {
        None
    };

    Score { value, band, confidence: conf, limiter, gates_failed: Vec::new() }
}

/// Оценка БЕЗ вклада стабильности — то, чем кормится [`StabilityTracker`].
///
/// Вынесено отдельно, чтобы метрика стабильности не зависела от самой себя:
/// иначе расхождение средних измеряло бы собственную обратную связь.
/// Ворота здесь не проверяются: трекеру нужна непрерывная кривая качества, а
/// не ступенька в ноль, — дисквалификацию учитывает [`score_with`].
pub fn base_score(h: &RouteHealth, sla: SlaClass) -> Option<f32> {
    let p = sla.profile();
    let q = qualities(h, &p, None, false)?;
    let w = effective_weights(sla, q.throughput.is_some(), false);
    Some(aggregate(&q, &w).0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::axis::Axis;
    use crate::ids::RouteId;
    use crate::metrics::{Probe, ProbeOutcome};

    const ШАГ: u64 = 5_000;

    /// Детерминированный генератор — тесты обязаны быть воспроизводимыми,
    /// а зависимость от системного ГПСЧ в ядре запрещена вообще.
    struct Ряд(u64);
    impl Ряд {
        fn new(seed: u64) -> Self {
            Ряд(seed)
        }
        /// Равномерно в 0..1.
        fn u(&mut self) -> f32 {
            self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            ((self.0 >> 33) as f32) / 2147483648.0
        }
    }

    /// Маршрут с задержкой около `mean` и равномерным разбросом ±`spread`.
    fn маршрут(mean: f32, spread: f32, n: u32, seed: u64) -> RouteHealth {
        let mut h = RouteHealth::new(RouteId::new("r"), Axis::RealTls);
        let mut r = Ряд::new(seed);
        for i in 0..n {
            let rtt = mean + spread * (2.0 * r.u() - 1.0);
            h.observe(&Probe {
                route: RouteId::new("r"),
                at: Instant(i as u64 * ШАГ),
                outcome: ProbeOutcome::Ok { rtt_ms: rtt },
            });
        }
        h
    }

    fn сейчас(h: &RouteHealth) -> Instant {
        h.last_sample_at.unwrap()
    }

    // ── центральное утверждение продукта ──

    #[test]
    fn джиттер_решает_для_интерактива_и_ничего_не_решает_для_потока() {
        // Узел ближе, но рваный, против узла дальше, но ровного.
        let mut быстрый_рваный = маршрут(35.0, 39.0, 200, 1);
        let mut дальний_ровный = маршрут(45.0, 4.5, 200, 2);
        // Полосу даём обоим одинаковую, чтобы она не влияла на разницу.
        быстрый_рваный.observe_throughput(100.0, 0);
        дальний_ровный.observe_throughput(100.0, 0);

        let (a, b) = (&быстрый_рваный, &дальний_ровный);
        // Сценарий проверяем по тому, что видит оценщик, а не по тому, что
        // задумано генератором: между ними стоит потоковый перцентиль со
        // своей погрешностью.
        assert!(a.rtt_p50.get().unwrap() < b.rtt_p50.get().unwrap(), "рваный обязан быть ближе");
        assert!(a.pdv_ms().unwrap() > 25.0, "разброс у рваного вышел {:?}", a.pdv_ms());
        assert!(b.pdv_ms().unwrap() < 5.0, "разброс у ровного вышел {:?}", b.pdv_ms());

        let ar = score(a, SlaClass::Realtime, сейчас(a), ШАГ);
        let br = score(b, SlaClass::Realtime, сейчас(b), ШАГ);
        let as_ = score(a, SlaClass::Stream, сейчас(a), ШАГ);
        let bs = score(b, SlaClass::Stream, сейчас(b), ШАГ);

        let разрыв_интерактив = br.value - ar.value;
        let разрыв_поток = (bs.value - as_.value).abs();

        // Для интерактива разрыв обязан быть БОЛЬШЕ порога переключения
        // (5 пунктов) — иначе гистерезис его проглотит и весь механизм
        // окажется бесполезным.
        assert!(
            разрыв_интерактив > 5.0,
            "интерактив: рваный {:.1}, ровный {:.1}",
            ar.value,
            br.value
        );
        // Для потока — заведомо МЕНЬШЕ порога: там разброс не решает
        // ничего, и переключаться из-за него незачем. Именно это значит
        // «не проигрывает»: проигрыш меньше цены разрыва сессий.
        assert!(
            разрыв_поток < 5.0,
            "поток: рваный {:.1}, ровный {:.1}",
            as_.value,
            bs.value
        );
        assert!(
            разрыв_интерактив > разрыв_поток * 5.0,
            "разрывы {:.2} и {:.2} — класс перестал что-то значить",
            разрыв_интерактив,
            разрыв_поток
        );
        assert_eq!(ar.limiter, MetricId::Jitter, "ограничитель назван неверно");
    }

    // ── ворота ──

    #[test]
    fn высокие_потери_дисквалифицируют_а_не_снижают_оценку() {
        let mut h = RouteHealth::new(RouteId::new("r"), Axis::QuicUdp);
        let mut r = Ряд::new(7);
        // 12% потерь при идеальной во всём остальном задержке.
        for i in 0..250u32 {
            let потеря = r.u() < 0.12;
            let o = if потеря {
                ProbeOutcome::Timeout
            } else {
                ProbeOutcome::Ok { rtt_ms: 25.0 + r.u() }
            };
            h.observe(&Probe { route: RouteId::new("r"), at: Instant(i as u64 * ШАГ), outcome: o });
        }
        h.observe_throughput(200.0, 0);

        let s = score(&h, SlaClass::Realtime, сейчас(&h), ШАГ);
        assert!(s.is_disqualified(), "маршрут с 12% потерь не дисквалифицирован");
        assert!(s.gates_failed.contains(&GateId::Loss), "сработали не те ворота: {:?}", s.gates_failed);
        assert_eq!(s.value, 0.0, "дисквалифицированный маршрут обязан давать ровно ноль");

        // И главное: без ворот арифметика оставила бы его вполне приличным.
        // Считаем ту же оценку по тем же метрикам, но минуя ворота.
        let без_ворот = {
            let p = SlaClass::Realtime.profile();
            let q = qualities(&h, &p, None, false).unwrap();
            let w = effective_weights(SlaClass::Realtime, true, false);
            aggregate(&q, &w).0
        };
        assert!(
            без_ворот > 50.0,
            "агрегация и без ворот дала {без_ворот:.1} — тест перестал доказывать нужное"
        );
    }

    #[test]
    fn ворота_недостижимые_при_текущем_числе_проб_не_применяются() {
        // Идеальный маршрут: десять проб, ни одной потери. Верхняя граница
        // Уилсона всё равно около 27% — ворота «>2%» провалили бы его за
        // незнание, а не за качество.
        let h = маршрут(30.0, 2.0, 10, 3);
        let s = score(&h, SlaClass::Realtime, сейчас(&h), ШАГ);
        assert!(
            !s.gates_failed.contains(&GateId::Loss),
            "безупречный маршрут дисквалифицирован за малую выборку"
        );
        // Но и в выбор он не попадёт — его держит уверенность, а не ворота.
        assert!(s.confidence < 0.5, "уверенность {:.2} на десяти пробах", s.confidence);
    }

    #[test]
    fn подмена_dns_дисквалифицирует_с_первой_пробы() {
        let mut h = маршрут(20.0, 1.0, 40, 4);
        assert!(!score(&h, SlaClass::Browse, сейчас(&h), ШАГ).is_disqualified());
        h.dns_tampered = true;
        let s = score(&h, SlaClass::Browse, сейчас(&h), ШАГ);
        assert!(s.gates_failed.contains(&GateId::DnsTampered));
        assert_eq!(s.value, 0.0);
    }

    #[test]
    fn неподтвержденный_выход_терпим_везде_кроме_чувствительного_класса() {
        let h = маршрут(20.0, 1.0, 40, 5);
        // ExitVerdict::Unknown — состояние по умолчанию.
        assert!(!score(&h, SlaClass::Browse, сейчас(&h), ШАГ).is_disqualified());
        let s = score(&h, SlaClass::Sensitive, сейчас(&h), ШАГ);
        assert!(
            s.gates_failed.contains(&GateId::ExitUnverified),
            "непроверяемый выход прошёл в чувствительный класс"
        );
    }

    // ── неизмеренное ≠ нулевое ──

    #[test]
    fn неизмеренная_полоса_не_равна_нулевой() {
        let без = маршрут(40.0, 5.0, 60, 6);
        let mut ноль = маршрут(40.0, 5.0, 60, 6);
        ноль.observe_throughput(0.0, 0);
        let mut сто = маршрут(40.0, 5.0, 60, 6);
        сто.observe_throughput(100.0, 0);

        let s_без = score(&без, SlaClass::Bulk, сейчас(&без), ШАГ);
        let s_ноль = score(&ноль, SlaClass::Bulk, сейчас(&ноль), ШАГ);
        let s_сто = score(&сто, SlaClass::Bulk, сейчас(&сто), ШАГ);

        assert!(
            s_без.value > s_ноль.value + 50.0,
            "«не измерено» {:.1} оказалось почти тем же, что «ноль мегабит» {:.1}",
            s_без.value,
            s_ноль.value
        );
        assert!(s_ноль.value < 5.0, "нулевая полоса дала {:.1}", s_ноль.value);
        // Но и бесплатным незнание не бывает: уверенность ниже измеренной.
        assert!(
            s_без.confidence < s_сто.confidence,
            "неизмеренная полоса не снизила уверенность: {:.2} против {:.2}",
            s_без.confidence,
            s_сто.confidence
        );
        // И ограничителем неизмеренная метрика назваться не может.
        assert_ne!(s_без.limiter, MetricId::Throughput);
    }

    // ── холодный старт ──

    #[test]
    fn на_холодном_старте_числа_нет() {
        let h = маршрут(30.0, 3.0, 3, 8);
        let s = score(&h, SlaClass::Browse, сейчас(&h), ШАГ);
        assert!(s.confidence < CONF_SHOW_BAND, "уверенность {:.2}", s.confidence);
        assert_eq!(s.display(), Display::Measuring, "холодный маршрут показал число");
        assert!(!s.is_disqualified(), "незнание выдано за дисквалификацию");
    }

    #[test]
    fn уверенность_растет_с_наблюдением_и_протухает_со_временем() {
        let восемь = маршрут(40.0, 5.0, 8, 9);
        let тридцать = маршрут(40.0, 5.0, 30, 9);
        let сто_двадцать = маршрут(40.0, 5.0, 120, 9);

        let c8 = confidence(&восемь, сейчас(&восемь), ШАГ);
        let c30 = confidence(&тридцать, сейчас(&тридцать), ШАГ);
        let c120 = confidence(&сто_двадцать, сейчас(&сто_двадцать), ШАГ);

        assert!(c8 < 0.05, "восемь проб дали уверенность {c8:.2}");
        assert!(c30 > c8 && c30 < 0.55, "тридцать проб дали {c30:.2}");
        assert!(c120 > 0.7, "сто двадцать проб дали {c120:.2}");

        // Возраст: через три интервала простоя доверие падает вдвое.
        let позже = сейчас(&сто_двадцать).plus_ms(3 * ШАГ);
        let c_старое = confidence(&сто_двадцать, позже, ШАГ);
        assert!(
            (c_старое / c120 - 0.5).abs() < 0.02,
            "протухание пошло не по половинному закону: {c_старое:.3} против {c120:.3}"
        );
    }

    #[test]
    fn при_средней_уверенности_показывается_диапазон_а_не_число() {
        // Подбираем наблюдение так, чтобы уверенность попала в середину.
        let h = маршрут(40.0, 12.0, 45, 10);
        let s = score(&h, SlaClass::Browse, сейчас(&h), ШАГ);
        assert!(
            (CONF_SHOW_BAND..CONF_SHOW_VALUE).contains(&s.confidence),
            "уверенность {:.2} вне середины — тест подобран неудачно",
            s.confidence
        );
        let (lo, hi) = s.band.expect("диапазона нет");
        assert!(lo < hi);
        // Ширина задана уверенностью: ±(12·(1−C)+1).
        let ожидаемая = 2.0 * (12.0 * (1.0 - s.confidence) + 1.0);
        assert!((hi - lo - ожидаемая).abs() < 0.5, "ширина {:.2}", hi - lo);
        // Центр — по пессимистичному прочтению, то есть НЕ выше числа.
        assert!(
            (lo + hi) / 2.0 <= s.value + 0.01,
            "центр диапазона {:.1} оказался оптимистичнее числа {:.1}",
            (lo + hi) / 2.0,
            s.value
        );
        assert!(matches!(s.display(), Display::Band { .. }));
    }

    // ── форма агрегации ──

    #[test]
    fn геометрия_наказывает_одну_плохую_метрику_сильнее_суммы() {
        // Всё идеально, кроме разброса задержки.
        let mut h = маршрут(35.0, 66.0, 200, 11);
        h.observe_throughput(200.0, 0);
        let p = SlaClass::Realtime.profile();
        let pdv = h.pdv_ms().unwrap();
        assert!(pdv > 40.0, "разброс вышел {pdv:.1} — тест не про то");

        let s = score(&h, SlaClass::Realtime, сейчас(&h), ШАГ);
        assert!(!s.is_disqualified(), "ворота вмешались: {:?}", s.gates_failed);

        // Эталонная СУММА по тем же нормированным метрикам и тем же весам.
        let w = effective_weights(SlaClass::Realtime, true, false);
        let q_rtt = hill(h.rtt_p50.get().unwrap(), X0_MS, p.x50_latency_ms, K_LATENCY);
        let q_jit = hill(pdv, X0_MS, p.x50_jitter_ms, K_JITTER);
        let q_loss = 1.0;
        let q_bw = hill_saturating(200.0, p.b50_mbps, K_THROUGHPUT);
        let сумма = 100.0 * (w.rtt * q_rtt + w.jitter * q_jit + w.loss * q_loss + w.throughput * q_bw);

        assert!(
            s.value < сумма - 15.0,
            "геометрия {:.1} почти не отличается от суммы {:.1} — компенсация не ограничена",
            s.value,
            сумма
        );
        assert!(q_jit < 0.2, "нормировка разброса дала {q_jit:.3}");
    }

    #[test]
    fn веса_после_перераспределения_дают_единицу() {
        for sla in SlaClass::ALL {
            assert!((sla.profile().weights.sum() - 1.0).abs() < 1e-5, "{sla:?}");
            for (tp, st) in [(true, true), (true, false), (false, true), (false, false)] {
                let w = effective_weights(sla, tp, st);
                assert!((w.sum() - 1.0).abs() < 1e-5, "{sla:?} {tp} {st}: {:?}", w);
            }
        }
    }

    #[test]
    fn кривая_хилла_дает_половину_в_колене_и_единицу_до_порога() {
        assert_eq!(hill(10.0, 20.0, 80.0, K_LATENCY), 1.0);
        assert_eq!(hill(20.0, 20.0, 80.0, K_LATENCY), 1.0);
        assert!((hill(80.0, 20.0, 80.0, K_LATENCY) - 0.5).abs() < 1e-5);
        // Крутизна: за коленом падение обязано быть быстрым, до колена —
        // почти незаметным. Ровно этим кривая отличается от линейной.
        let линейная = |x: f32| (1.0 - (x - 20.0) / 60.0).clamp(0.0, 1.0);
        assert!(hill(40.0, 20.0, 80.0, K_LATENCY) > линейная(40.0) + 0.2);
        assert!(hill(140.0, 20.0, 80.0, K_LATENCY) < 0.12);
        assert!(hill(200.0, 20.0, 80.0, K_LATENCY) < 0.05);
    }

    #[test]
    fn пачечные_потери_вреднее_разрозненных() {
        // Одна и та же доля потерь, разная структура. Модель Гилберта
        // обязана различать их, иначе коэффициент пачечности бесполезен.
        let ровно = loss_quality(0.05, 1.0, 8.0);
        let пачками = loss_quality(0.05, 2.0, 8.0);
        assert!(пачками < ровно, "пачки {пачками:.3} не хуже россыпи {ровно:.3}");
        // И терпимость класса значит именно то, что должна.
        assert!(loss_quality(0.05, 1.0, 40.0) > loss_quality(0.05, 1.0, 8.0));
    }

    #[test]
    fn стабильность_измеряется_только_при_наличии_истории() {
        let mut h = маршрут(40.0, 5.0, 60, 12);
        // Чувствительный класс требует подтверждённого выхода — иначе тест
        // упрётся в ворота вместо стабильности.
        h.verified_exit = ExitVerdict::Confirmed;
        // Без трекера вес стабильности перераспределяется.
        let без = effective_weights(SlaClass::Sensitive, false, false);
        assert_eq!(без.stability, 0.0);

        // С трекером, которому скормили мигающую оценку, стабильность
        // становится измеренной — и низкой.
        let mut t = StabilityTracker::new();
        for i in 0..40u64 {
            let v = if i % 2 == 0 { 95.0 } else { 45.0 };
            t.observe(v, Instant(i * ШАГ));
        }
        assert!(t.divergence().unwrap() > 5.0, "расхождение {:?}", t.divergence());

        let ровный = {
            let mut t = StabilityTracker::new();
            for i in 0..40u64 {
                t.observe(90.0, Instant(i * ШАГ));
            }
            t
        };
        let s_мигающий = score_with(&h, SlaClass::Sensitive, сейчас(&h), ШАГ, Some(&t));
        let s_ровный = score_with(&h, SlaClass::Sensitive, сейчас(&h), ШАГ, Some(&ровный));
        assert!(
            s_мигающий.value < s_ровный.value - 5.0,
            "мигающий {:.1} против ровного {:.1}",
            s_мигающий.value,
            s_ровный.value
        );
        assert_eq!(s_мигающий.limiter, MetricId::Stability);
    }

    #[test]
    fn базовая_оценка_не_зависит_от_стабильности() {
        // Иначе метрика стабильности измеряла бы собственную обратную связь.
        let mut h = маршрут(40.0, 5.0, 60, 13);
        h.verified_exit = ExitVerdict::Confirmed;
        let mut t = StabilityTracker::new();
        for i in 0..40u64 {
            t.observe(if i % 2 == 0 { 90.0 } else { 40.0 }, Instant(i * ШАГ));
        }
        let a = base_score(&h, SlaClass::Sensitive).unwrap();
        let b = base_score(&h, SlaClass::Sensitive).unwrap();
        assert_eq!(a, b);
        // И она заведомо выше оценки с учётом мигания.
        let s = score_with(&h, SlaClass::Sensitive, сейчас(&h), ШАГ, Some(&t));
        assert!(a > s.value);
    }
}
