//! Контур решений: из накопленного здоровья и политики — что сделать прямо
//! сейчас.
//!
//! Модуль ничего не исполняет и ни с кем не разговаривает. Он получает
//! снимок измерений, состояние полос и время — и возвращает список действий
//! с объяснением каждого. Исполнитель (Clash API, XPC, что угодно) живёт
//! этажом выше и о существовании этого кода знать не обязан.
//!
//! Три свойства, которые здесь важнее остального:
//!
//! 1. **Факты сильнее чисел.** Оценка не имеет права удержать человека на
//!    мёртвом маршруте. Три потери подряд, два провала рукопожатия, выход
//!    мимо узла, подмена DNS, утечка IPv6 — это факты, и они обходят порог,
//!    выдержку и остывание целиком.
//! 2. **Виновата бывает сеть, а не сервер.** Если одна ось обхода мертва на
//!    разных узлах — это фильтрация, а не поломка железа. Штрафуется ОСЬ.
//!    Ни один известный нам клиент так не умеет: все они наказывают узлы и
//!    потом ждут, пока исправное «выздоровеет».
//! 3. **Прямой выход не подставляется никогда.** Когда живых кандидатов не
//!    осталось, полоса уходит в своё `on_empty`, а тип [`OnEmpty`] прямого
//!    выхода не содержит и содержать не может.

use std::collections::{BTreeMap, BTreeSet};

use serde::{Deserialize, Serialize};

use crate::axis::Axis;
use crate::damper::{self, DamperState};
use crate::ids::{LaneId, NodeId, RouteId};
use crate::lane::{Lane, OnEmpty, SwitchMode};
use crate::metrics::{ExitVerdict, RouteHealth};
use crate::route::Route;
use crate::score;
use crate::Instant;

// ─────────────────────────────── Константы ───────────────────────────────

/// Порог уверенности для участия в выборе. Источник: раздел 6.1 документа
/// («наша константа»).
///
/// Смысл прямой: маршрут, о котором мы ещё ничего не знаем, не должен
/// выигрывать сравнение за счёт незнания. При шаге проб 5 с порог берётся
/// примерно к двум с половиной минутам наблюдения — медленно, но
/// альтернатива не «быстро», а «уверенно и неправильно».
pub const CONF_FLOOR: f32 = 0.5;

/// Сколько потерь подряд считается аварией. Источник: раздел 6.3 документа.
pub const LOST_STREAK_EMERGENCY: u32 = 3;

/// Сколько РАЗНЫХ узлов должны потерять ось, чтобы обвинять сеть, а не узлы.
/// Источник: раздел 6.3 («не менее двух»).
///
/// Двух хватает: одновременный отказ одной и той же оси на двух независимых
/// серверах в одну минуту куда менее вероятен, чем одна общая причина по
/// дороге.
pub const AXIS_DEAD_MIN_NODES: usize = 2;

/// Осевой штраф за один цикл, пункты оценки. НАША константа: документ
/// называет её `AXIS_PENALTY`, но числа не приводит.
///
/// Выбрана из двух соображений. Потолок осевого штрафа — 100 пунктов
/// ([`damper::AXIS_PENALTY_CAP`]), значит четыре подряд подтверждённых
/// вердикта доводят ось до потолка; при шаге проб 5 с это 20 секунд — тот
/// же порядок, что и реакция аварийного пути (три потери подряд ≈ 15 с).
/// С другой стороны, одиночный ошибочный вердикт стоит 25 пунктов: этого
/// хватает, чтобы сместить предпочтение, и не хватает, чтобы похоронить
/// ось — а хоронить её нельзя, она остаётся последней надеждой, если
/// умрут остальные.
pub const AXIS_PENALTY_POINTS: f32 = 25.0;

/// Через сколько интервалов проб измерение перестаёт годиться для осевого
/// вердикта. НАША константа.
///
/// Привязана к множителю возраста в уверенности: `C_age = 0.5^(возраст /
/// (3·интервал))`, то есть на шести интервалах от возраста остаётся 0.25.
/// Судить по такому замеру о том, режет ли сеть целую ось, уже нельзя —
/// он говорит о прошлой сети, а не о нынешней.
pub const STALE_AFTER_INTERVALS: u64 = 6;

/// Половина мёртвой зоны гистерезиса A3, пункты. НАША константа: документ
/// задаёт форму условия (раздел 6.2), но значения `Hys` не называет.
///
/// Взята как остаточный шум СГЛАЖЕННОЙ оценки. Измеренный шум сырой оценки
/// — 4.4 пункта при тридцати пробах (раздел 6.1); экспоненциальное
/// сглаживание с α=1/8 давит его в `√(α/(2−α)) ≈ 0.26` раза, то есть до
/// одного пункта. Мёртвая зона ровно в этот пункт и нужна: кандидат,
/// колеблющийся вокруг порога, не сбрасывает счётчик выдержки на каждом
/// втором замере — иначе переключение не состоялось бы никогда, даже когда
/// оно объективно нужно.
pub const HYS_POINTS: f32 = 1.0;

/// Полоса-заглушка для причин, которые не принадлежат ни одной полосе.
///
/// Осевой вердикт — про сеть целиком, а не про полосу; но [`DecisionReason`]
/// обязан назвать полосу, потому что в интерфейсе причины показываются
/// рядом с полосами. Сюда попадают только общие вердикты.
pub const GLOBAL_LANE: &str = "*";

/// Идентификатор [`GLOBAL_LANE`] как значение.
pub fn global_lane() -> LaneId {
    LaneId::new(GLOBAL_LANE)
}

// ──────────────────────────────── Причины ────────────────────────────────

/// Род причины. Машинная половина объяснения: по ней интерфейс выбирает
/// значок и решает, показывать ли уведомление.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ReasonKind {
    /// Первый выбор для полосы: держаться было не за что.
    Initial,
    /// Сравнение оценок. Либо переключились, потому что кандидат лучше на
    /// порог, либо остались, потому что «лучше» до порога не дотянуло —
    /// человеку одинаково важны оба случая.
    Better,
    /// Сработал ФАКТ, а не число: маршрут мёртв или скомпрометирован.
    EmergencyFact,
    /// Ось обхода не проходит в этой сети.
    AxisDead,
    /// Маршрут отставлен демпфером за дребезг.
    Suppressed,
    /// Выбор закреплён человеком: закрепление принято, удержано или
    /// отклонено политикой — что именно, сказано во фразе.
    UserPinned,
    /// Смена режима. Этот модуль такой причины не выдаёт — её выдаёт контур
    /// режимов этажом выше; род объявлен здесь, чтобы словарь объяснений
    /// был один на всю систему.
    ModeChanged,
    /// Живого кандидата нет — ни на выбор, ни в запас.
    NoCandidate,
    /// Подавление снято, потому что иначе полоса осталась бы вообще без
    /// маршрутов.
    DamperOverridden,
}

/// Объяснение одного решения.
///
/// Без объяснения автоматика читается как своеволие, и её выключают. Поэтому
/// человеческая фраза — обязательное поле, а не удобство: любое действие
/// этого модуля несёт её с собой.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct DecisionReason {
    pub kind: ReasonKind,
    pub lane: LaneId,
    /// Одна фраза для человека, по-русски.
    pub human_ru: String,
}

impl DecisionReason {
    fn new(kind: ReasonKind, lane: &LaneId, text: String) -> Self {
        Self { kind, lane: lane.clone(), human_ru: text }
    }
}

/// Что исполнителю сделать.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "do", rename_all = "snake_case")]
pub enum Actuation {
    /// Поставить полосе маршрут.
    SelectLane { lane: LaneId, route: RouteId, reason: DecisionReason },
    /// Оборвать живые соединения полосы.
    ///
    /// Отдельным действием, а не флагом выбора: переключение селектора само
    /// по себе соединения НЕ рвёт, и это асимметрия с автопереизбранием
    /// ядра. Она нам на руку — «дотечь или оборвать» становится решением
    /// политики, а не свойством конфига.
    Drain { lane: LaneId },
    /// Живых кандидатов нет. Действие определяет полоса, и прямого выхода
    /// среди вариантов нет по типу.
    GoEmpty { lane: LaneId, action: OnEmpty, reason: DecisionReason },
}

// ───────────────────────────── Состояние полос ─────────────────────────────

/// Что известно о полосе снаружи ядра.
///
/// Ядро состояние не хранит: его ведёт исполнитель, который единственный
/// знает, применилось ли предыдущее решение на самом деле.
///
/// Намеренно НЕ `Serialize` — по той же причине, по какой не сериализуется
/// [`DamperState`]: срок закрепления выражен в монотонных часах, которые при
/// перезапуске обнуляются. Записанный на диск срок после перезагрузки
/// означал бы либо «закреплено навечно», либо «закрепление истекло сразу», и
/// оба варианта хуже, чем спросить человека заново.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct LaneState {
    pub current: Option<RouteId>,
    /// Маршрут и срок закрепления. Закрепление ставит человек, и оно сильнее
    /// всей математики — но не сильнее фактов.
    pub pin: Option<(RouteId, Instant)>,
}

const NO_STATE: LaneState = LaneState { current: None, pin: None };

// ──────────────────────────────── Снимок ────────────────────────────────

struct Entry {
    route: Route,
    health: RouteHealth,
    country: String,
}

/// Снимок мира на момент решения: маршруты, их здоровье и страны узлов.
///
/// Именно снимок, а не живая структура: решение обязано быть чистой функцией
/// от входа, иначе его не прогнать на записанном логе измерений и гистерезис
/// придётся настраивать вслепую.
#[derive(Default)]
pub struct Snapshot {
    /// `BTreeMap`, а не `HashMap`: порядок обхода обязан быть
    /// воспроизводимым, иначе один и тот же лог даст разные решения при
    /// равных оценках.
    entries: BTreeMap<RouteId, Entry>,
}

impl Snapshot {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn insert(&mut self, route: Route, health: RouteHealth, country: String) {
        self.entries.insert(route.id.clone(), Entry { route, health, country });
    }

    pub fn health(&self, id: &RouteId) -> Option<&RouteHealth> {
        self.entries.get(id).map(|e| &e.health)
    }

    pub fn route(&self, id: &RouteId) -> Option<&Route> {
        self.entries.get(id).map(|e| &e.route)
    }

    /// Страна узла. Нужна и требованиям полосы, и человеческим фразам.
    pub fn country(&self, id: &RouteId) -> Option<&str> {
        self.entries.get(id).map(|e| e.country.as_str())
    }

    pub fn routes(&self) -> impl Iterator<Item = (&Route, &RouteHealth)> {
        self.entries.values().map(|e| (&e.route, &e.health))
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
}

// ─────────────────────────── Аварийные факты ───────────────────────────

/// Факт, при котором держаться за маршрут нельзя ни при какой оценке.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EmergencyFact {
    /// Три пробы подряд без ответа.
    ThreeProbesLost,
    /// Рукопожатие не проходит второй раз подряд.
    HandshakeFailed,
    /// Трафик вышел не через тот узел. Худший случай из всех: туннель
    /// выглядит рабочим, а человек раскрыт.
    ExitMismatch,
    /// Ответы DNS подменяются.
    DnsTampered,
    /// Часть трафика утекает мимо туннеля по IPv6.
    Ipv6Leak,
}

impl EmergencyFact {
    pub fn human_ru(self) -> &'static str {
        match self {
            EmergencyFact::ThreeProbesLost => "три пробы подряд без ответа",
            EmergencyFact::HandshakeFailed => "рукопожатие не проходит второй раз подряд",
            EmergencyFact::ExitMismatch => "трафик вышел не через тот узел",
            EmergencyFact::DnsTampered => "ответы DNS подменяются",
            EmergencyFact::Ipv6Leak => "часть трафика утекает мимо туннеля по IPv6",
        }
    }
}

/// Есть ли на маршруте аварийный факт.
///
/// Порядок проверок — по убыванию тяжести последствий, а не по удобству:
/// первым назовут то, что покажут человеку.
///
/// `ExitVerdict::Unknown` аварией НЕ является: «подтвердить нечем» — не то
/// же самое, что «вышло мимо». У чужого узла из подписки просто нет ключа
/// подтверждения, и хоронить его за это нельзя (для класса Sensitive это
/// всё равно ворота — там цена ошибки другая).
pub fn emergency_fact(h: &RouteHealth) -> Option<EmergencyFact> {
    if h.consecutive_lost >= LOST_STREAK_EMERGENCY {
        return Some(EmergencyFact::ThreeProbesLost);
    }
    if h.handshake_failed_streak >= score::HANDSHAKE_FAIL_STREAK {
        return Some(EmergencyFact::HandshakeFailed);
    }
    if h.verified_exit == ExitVerdict::Mismatch {
        return Some(EmergencyFact::ExitMismatch);
    }
    if h.dns_tampered {
        return Some(EmergencyFact::DnsTampered);
    }
    if h.ipv6_leak {
        return Some(EmergencyFact::Ipv6Leak);
    }
    None
}

// ─────────────────────────── Осевой вердикт ───────────────────────────

/// Жив ли маршрут — по ФАКТАМ, без арифметики.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
enum Liveness {
    Alive,
    Dead,
    /// Не мерили или мерили слишком давно. Молчание — не свидетельство.
    Unknown,
}

fn liveness(h: &RouteHealth, now: Instant, probe_interval_ms: u64) -> Liveness {
    let Some(last) = h.last_sample_at else {
        return Liveness::Unknown;
    };
    if h.sample_count == 0 {
        return Liveness::Unknown;
    }
    if now.since(last) > probe_interval_ms.max(1).saturating_mul(STALE_AFTER_INTERVALS) {
        return Liveness::Unknown;
    }
    // Про ОСЬ свидетельствуют только отказы транспорта: не дошло и не
    // пожали руки. Подмена DNS и выход мимо узла — не про способ пройти,
    // они случаются и на совершенно исправном транспорте; они
    // дисквалифицируют маршрут воротами, но обвинять по ним ось нельзя.
    if h.consecutive_lost >= LOST_STREAK_EMERGENCY
        || h.handshake_failed_streak >= score::HANDSHAKE_FAIL_STREAK
    {
        Liveness::Dead
    } else {
        Liveness::Alive
    }
}

/// Вердикт по одной оси: где она мертва и где жива.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct AxisVerdict {
    /// Узлы, на которых эта ось мертва целиком (все её маршруты там мертвы).
    pub dead_nodes: BTreeSet<NodeId>,
    /// Узлы, где хоть один маршрут этой оси жив.
    pub alive_nodes: BTreeSet<NodeId>,
}

impl AxisVerdict {
    /// Похоже ли это на работу сети, а не на поломку серверов.
    ///
    /// ОТСТУПЛЕНИЕ от буквы псевдокода: там условие названо
    /// `dead_everywhere` — ось мертва на ВСЕХ узлах. Здесь достаточно двух
    /// разных узлов, и вот почему.
    ///
    /// Во-первых, двух свидетельств уже хватает: одновременный отказ одной и
    /// той же оси на двух независимых серверах в одну минуту куда менее
    /// вероятен, чем одна общая причина по дороге.
    /// Во-вторых — и это главное — ждать смерти на ВСЕХ узлах значит ждать
    /// последнего: к тому моменту переносить предпочтение уже некуда, и весь
    /// смысл осевого вердикта (успеть до того, как ось кончится целиком)
    /// теряется.
    /// В-третьих, цена ошибки мала: вердикт не исключает ось, а понижает её,
    /// и живой маршрут на третьем узле остаётся в игре — просто перестаёт
    /// быть первым выбором.
    pub fn looks_like_network(&self) -> bool {
        self.dead_nodes.len() >= AXIS_DEAD_MIN_NODES
    }
}

/// Разложить снимок по осям. Считается ОДИН раз на все полосы: вердикт про
/// сеть, а полосы тут ни при чём.
pub fn classify_axes(
    snap: &Snapshot,
    now: Instant,
    probe_interval_ms: u64,
) -> BTreeMap<Axis, AxisVerdict> {
    // Сначала по узлам: ось считается мёртвой на узле, только если там нет
    // ни одного её живого маршрута. Иначе один упавший инстанс из двух на
    // ноде выглядел бы фильтрацией целой оси.
    let mut per_node: BTreeMap<(Axis, NodeId), (bool, bool)> = BTreeMap::new();
    for (route, health) in snap.routes() {
        let key = (route.axis, route.node.clone());
        let cell = per_node.entry(key).or_insert((false, false));
        match liveness(health, now, probe_interval_ms) {
            Liveness::Alive => cell.0 = true,
            Liveness::Dead => cell.1 = true,
            Liveness::Unknown => {}
        }
    }

    let mut out: BTreeMap<Axis, AxisVerdict> = BTreeMap::new();
    for ((axis, node), (alive, dead)) in per_node {
        let v = out.entry(axis).or_default();
        if alive {
            v.alive_nodes.insert(node);
        } else if dead {
            v.dead_nodes.insert(node);
        }
    }
    out
}

// ──────────────────────── Ключ сглаженной серии ────────────────────────

/// Разделитель ключа. ASCII Unit Separator: в идентификаторах узлов, полос и
/// транспортов он не встречается, а глазами в логе всё ещё читается.
const KEY_SEP: char = '\u{1f}';

/// Ключ сглаженной серии оценок: ПОЛОСА и маршрут.
///
/// ОТСТУПЛЕНИЕ от псевдокода 6.3, где серия ведётся по одному маршруту.
/// Причина: один и тот же маршрут обслуживает несколько полос, а оценка
/// считается под КЛАСС полосы. Тот же lt.trojan даёт для `call` (Realtime)
/// и для `bulk` (Bulk) разные числа — веса различаются вчетверо. Общая на
/// две полосы EWMA смешивала бы их в среднее, не равное ни одному из них, а
/// σ, из которой берётся порог переключения, раздувалась бы РАЗНИЦЕЙ
/// КЛАССОВ, а не шумом сети: гистерезис настраивался бы по несуществующей
/// величине.
///
/// Штрафы за дребезг и подавление, наоборот, ведутся по настоящему
/// [`RouteId`]: флап — свойство маршрута, а не полосы.
fn series_key(lane: &LaneId, route: &RouteId) -> RouteId {
    RouteId::new(format!("{}{}{}", lane.as_str(), KEY_SEP, route.as_str()))
}

/// Забыть про маршрут ВЕЗДЕ: и штрафы, и сглаженные серии всех полос.
///
/// Нужно при смене сети: накопленное через другой канал ни с чем не
/// сравнимо. [`DamperState::forget_route`] в одиночку не годится — про
/// ключи серий (см. [`series_key`]) он не знает.
pub fn forget_route_everywhere(damper: &mut DamperState, lanes: &[Lane], route: &RouteId) {
    damper.forget_route(route);
    for lane in lanes {
        damper.forget_route(&series_key(&lane.id, route));
    }
}

// ──────────────────────────── Отбор кандидатов ────────────────────────────

/// Годится ли маршрут этой полосе по политике — до всякой оценки.
///
/// Проверка замыкания экспозиции здесь ДУБЛИРУЕТ проверку компилятора планов
/// намеренно. Защита периметра не имеет права зависеть от того, что кто-то
/// другой не ошибся: цена ошибки — трафик, вышедший открытым.
fn eligible(lane: &Lane, snap: &Snapshot, id: &RouteId) -> bool {
    let Some(route) = snap.route(id) else {
        return false;
    };
    let Some(country) = snap.country(id) else {
        return false;
    };
    if !lane.allow.allows(&route.exposure) {
        return false;
    }
    lane.need.satisfied_by(route, country)
}

/// Итог отбора: кто участвует в выборе и что демпфер отставил.
struct Qualified {
    /// Кандидаты после всех фильтров.
    ready: Vec<RouteId>,
    /// Отставленные демпфером (для объяснения человеку).
    suppressed: Vec<RouteId>,
    /// Подавление пришлось снять: иначе кандидатов не осталось бы вовсе.
    damper_overridden: bool,
}

/// Ворота → уверенность → подавление → требования полосы.
///
/// Требования полосы и экспозиция проверяются ПЕРВЫМИ, хотя в псевдокоде
/// стоят последними: на порядок результата это не влияет, а стоит дешевле —
/// оценка не считается для маршрутов, которые полосе всё равно не подходят.
fn qualify(
    lane: &Lane,
    candidates: &[RouteId],
    snap: &Snapshot,
    damper: &DamperState,
    now: Instant,
    probe_interval_ms: u64,
) -> Qualified {
    let mut passing: Vec<RouteId> = Vec::new();
    for id in candidates {
        if !eligible(lane, snap, id) {
            continue;
        }
        let Some(h) = snap.health(id) else { continue };
        let s = score::score(h, lane.sla, now, probe_interval_ms);
        if s.is_disqualified() {
            continue;
        }
        // Уверенность берётся ИЗ ОЦЕНКИ, а не голая уверенность измерений:
        // сравниваем-то мы оценки, и если половина веса класса приходится
        // на неизмеренное, доверия к числу меньше — score.rs это уже учёл
        // множителем. Порог от этого чуть строже, чем в псевдокоде.
        if s.confidence < CONF_FLOOR {
            continue;
        }
        passing.push(id.clone());
    }

    let suppressed: Vec<RouteId> =
        passing.iter().filter(|r| damper.is_suppressed(r, now)).cloned().collect();
    let ready: Vec<RouteId> =
        passing.iter().filter(|r| !damper.is_suppressed(r, now)).cloned().collect();

    // ИНВАРИАНТ: демпфер НИКОГДА не исключает последний живой маршрут.
    // У нас три ноды, а не кластер из сотен: демпфер, доведённый до
    // логического конца, устроил бы отказ в обслуживании сам себе.
    //
    // Снимается при этом ТОЛЬКО подавление. В псевдокоде повторный отбор
    // теряет заодно и требования полосы — это описка, и повторять её нельзя:
    // требования держат периметр (замкнутость экспозиции) и политику
    // (запрещённые страны, обязательный UDP). Демпфер — наша защита от
    // дребезга, а не право обойти политику, когда стало неудобно.
    if ready.is_empty() && !passing.is_empty() {
        return Qualified { ready: passing, suppressed, damper_overridden: true };
    }
    Qualified { ready, suppressed, damper_overridden: false }
}

// ──────────────────────────── Оценка и ранг ────────────────────────────

/// Обновить сглаженные серии полосы.
///
/// Ноль за провал ворот маршрут получает честно и выбирается из него теми же
/// измерениями, что и все: сглаженная оценка обязана отражать и плохие
/// времена, иначе «вернувшийся» маршрут выигрывает сравнение прошлыми
/// заслугами.
fn refresh_series(
    lane: &Lane,
    ids: &BTreeSet<RouteId>,
    snap: &Snapshot,
    damper: &mut DamperState,
    now: Instant,
    probe_interval_ms: u64,
) {
    for id in ids {
        let Some(h) = snap.health(id) else { continue };
        let raw = score::score(h, lane.sla, now, probe_interval_ms).value;
        damper.smoothed(&series_key(&lane.id, id), raw, now);
    }
}

/// Чьи серии полоса обязана обновить в этом цикле: все пригодные ей
/// маршруты плюс действующий, даже если он выбыл из кандидатов.
fn warm(lane: &Lane, candidates: &[RouteId], snap: &Snapshot, st: &LaneState) -> BTreeSet<RouteId> {
    let mut ids: BTreeSet<RouteId> =
        candidates.iter().filter(|id| eligible(lane, snap, id)).cloned().collect();
    if let Some(cur) = st.current.clone() {
        ids.insert(cur);
    }
    ids
}

/// Ранжировать маршруты по сглаженной оценке за вычетом осевого штрафа.
///
/// Осевой штраф вычитается ЗДЕСЬ, а не подмешивается в сглаживание: он не
/// свойство маршрута и должен исчезнуть вместе с вердиктом, а не тянуться
/// за ним ещё один период полураспада EWMA.
fn ranked(
    lane: &Lane,
    ids: &[RouteId],
    snap: &Snapshot,
    damper: &DamperState,
    now: Instant,
    probe_interval_ms: u64,
) -> Vec<(RouteId, f32)> {
    let mut v: Vec<(RouteId, f32)> = ids
        .iter()
        .filter_map(|id| {
            let axis = snap.route(id)?.axis;
            let s = match damper.smoothed_of(&series_key(&lane.id, id)) {
                Some(s) => s,
                None => score::score(snap.health(id)?, lane.sla, now, probe_interval_ms).value,
            };
            Some((id.clone(), s - damper.axis_penalty_points(axis, now)))
        })
        .collect();
    // Тай-брейк по идентификатору: при равных оценках решение обязано быть
    // одинаковым от прогона к прогону, иначе отладка гистерезиса
    // превращается в гадание.
    v.sort_by(|a, b| b.1.total_cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
    v
}

/// Первый в ранжированном списке, кто стоит на ДРУГОЙ оси, чем лидер.
fn first_on_other_axis(ranked: &[(RouteId, f32)], snap: &Snapshot) -> Option<RouteId> {
    let lead_axis = snap.route(&ranked.first()?.0)?.axis;
    ranked
        .iter()
        .skip(1)
        .find(|(r, _)| snap.route(r).map(|x| x.axis) != Some(lead_axis))
        .map(|(r, _)| r.clone())
}

/// Горячий резерв полосы: лучший из живых кандидатов на ДРУГОЙ оси, чем
/// действующий лидер.
///
/// Резерв на той же оси резервом не является — его убьёт ровно то же самое,
/// что убьёт лидера. Именно поэтому переключение с Hysteria2 на TUIC в сети,
/// которая режет QUIC, не даёт ничего.
///
/// Функция читающая: она ничего не меняет и годится измерительному слою как
/// подсказка, какой маршрут держать прогретым.
pub fn hot_standby(
    lane: &Lane,
    candidates: &[RouteId],
    snap: &Snapshot,
    damper: &DamperState,
    now: Instant,
    probe_interval_ms: u64,
) -> Option<RouteId> {
    let q = qualify(lane, candidates, snap, damper, now, probe_interval_ms);
    let r = ranked(lane, &q.ready, snap, damper, now, probe_interval_ms);
    first_on_other_axis(&r, snap)
}

// ──────────────────────────── Человеческие фразы ────────────────────────────

/// Падеж. Без него фразы звучат машинно: «ушли с Литва».
#[derive(Copy, Clone, Debug)]
enum Case {
    Nom,
    Gen,
    Acc,
}

/// Имя страны в трёх падежах.
///
/// Это НЕ словарь стран, а три страны, которые у нас есть. Для всех прочих
/// возвращается код: показать код честнее, чем склонять наугад и получить
/// «ушли с Нидерландыа».
fn country_form(code: &str, case: Case) -> &str {
    match (code, case) {
        ("LT", Case::Nom) => "Литва",
        ("LT", Case::Gen) => "Литвы",
        ("LT", Case::Acc) => "Литву",
        ("US", _) => "США",
        ("FI", Case::Nom) => "Финляндия",
        ("FI", Case::Gen) => "Финляндии",
        ("FI", Case::Acc) => "Финляндию",
        (other, _) => other,
    }
}

/// «Литвы (lt.trojan.8443)» — страна для человека, идентификатор для
/// диагностики. Интерфейс при желании обрежет по скобке, а в логе останется
/// то, по чему можно искать.
fn place(snap: &Snapshot, id: &RouteId, case: Case) -> String {
    match snap.country(id) {
        Some(c) if !c.is_empty() => format!("{} ({})", country_form(c, case), id),
        _ => id.to_string(),
    }
}

fn on_empty_phrase(lane: &Lane) -> String {
    match &lane.on_empty {
        OnEmpty::Block => format!(
            "живых маршрутов у полосы «{}» не осталось — трафик блокируется; \
             прямой выход не подставляется никогда",
            lane.title
        ),
        OnEmpty::HoldLast => format!(
            "живых маршрутов у полосы «{}» не осталось — держим прежний и честно \
             говорим, что он не проверен",
            lane.title
        ),
        OnEmpty::Fallback { lane: to } => format!(
            "живых маршрутов у полосы «{}» не осталось — уходим в запасную полосу «{}»",
            lane.title, to
        ),
    }
}

// ──────────────────────────── Аварийный выбор ────────────────────────────

/// Кем заменить мёртвый маршрут.
///
/// Порог уверенности здесь СНЯТ намеренно. Он защищает от переключения на
/// непроверенный маршрут ради мнимого выигрыша — а здесь выигрыш не мнимый:
/// текущий путь мёртв. Требовать 0.5 значило бы держать человека на мёртвом
/// маршруте те две с половиной минуты, что новый набирает статистику.
///
/// Ворота и требования полосы, наоборот, остаются: это не про качество, а
/// про пригодность и про периметр.
fn pick_emergency(
    lane: &Lane,
    candidates: &[RouteId],
    snap: &Snapshot,
    damper: &DamperState,
    now: Instant,
    probe_interval_ms: u64,
    current: &RouteId,
) -> Option<RouteId> {
    let current_axis = snap.route(current).map(|r| r.axis);

    let mut good: Vec<(bool, bool, f32, RouteId)> = Vec::new();
    for id in candidates {
        if id == current || !eligible(lane, snap, id) {
            continue;
        }
        let Some(h) = snap.health(id) else { continue };
        // На другой мёртвый маршрут прыгать бессмысленно.
        if emergency_fact(h).is_some() {
            continue;
        }
        if score::score(h, lane.sla, now, probe_interval_ms).is_disqualified() {
            continue;
        }
        let Some(axis) = snap.route(id).map(|r| r.axis) else { continue };
        let s = damper
            .smoothed_of(&series_key(&lane.id, id))
            .unwrap_or_else(|| score::score(h, lane.sla, now, probe_interval_ms).value)
            - damper.axis_penalty_points(axis, now);
        // Ключ сортировки: сначала не отставленные демпфером, затем те, что
        // стоят на ДРУГОЙ оси (если сеть убила способ пройти, соседний
        // маршрут той же оси умрёт следом), и только потом по оценке.
        good.push((
            damper.is_suppressed(id, now),
            current_axis == Some(axis),
            s,
            id.clone(),
        ));
    }

    good.sort_by(|a, b| {
        a.0.cmp(&b.0)
            .then_with(|| a.1.cmp(&b.1))
            .then_with(|| b.2.total_cmp(&a.2))
            .then_with(|| a.3.cmp(&b.3))
    });
    good.into_iter().next().map(|x| x.3)
}

// ────────────────────────────── Главный цикл ──────────────────────────────

/// Свести желаемое с действительным.
///
/// Порядок шагов — из раздела 6.3 документа, и ни один нельзя переставить:
/// осевой вердикт считается до полос, факты идут раньше закрепления,
/// закрепление раньше математики, инвариант «последний живой» — раньше
/// пустоты.
///
/// Возвращаются действия и ПОЛНЫЙ журнал причин: причина, уехавшая внутри
/// действия, продублирована в журнале — исполнителю нужна своя, а интерфейсу
/// сплошная лента, по которой человек читает, что происходило.
pub fn reconcile(
    lanes: &[Lane],
    candidates: &BTreeMap<LaneId, Vec<RouteId>>,
    snap: &Snapshot,
    state: &BTreeMap<LaneId, LaneState>,
    damper: &mut DamperState,
    now: Instant,
    probe_interval_ms: u64,
) -> (Vec<Actuation>, Vec<DecisionReason>) {
    let mut acts: Vec<Actuation> = Vec::new();
    let mut reasons: Vec<DecisionReason> = Vec::new();

    // ── 0. ОСЕВОЙ ВЕРДИКТ. Считается ОДИН раз на все полосы.
    // Если ось мертва на РАЗНЫХ узлах — виновата сеть, а не узлы: узлы не
    // штрафуются, штрафуется ОСЬ. Так выглядит фильтрация QUIC, при которой
    // Hysteria2 и TUIC ложатся разом, а TCP-оси дают свои сорок мегабит.
    let verdicts = classify_axes(snap, now, probe_interval_ms);
    for (axis, v) in &verdicts {
        if !v.looks_like_network() {
            continue;
        }
        // Условие из таблицы 6.4: «все маршруты оси мертвы на ≥2 разных
        // узлах, ПРИ ЭТОМ TCP-оси живы». Без второй половины вердикт врёт:
        // когда мертво вообще всё, виновата не ось, а отсутствие сети, и
        // фраза «в этой сети не проходит QUIC» была бы неправдой — а штраф
        // всё равно ничего бы не изменил, потому что вычитается у всех.
        let alive_elsewhere =
            verdicts.iter().any(|(a, w)| a != axis && !w.alive_nodes.is_empty());
        if !alive_elsewhere {
            continue;
        }
        damper.penalize_axis(*axis, AXIS_PENALTY_POINTS, now);
        reasons.push(DecisionReason::new(
            ReasonKind::AxisDead,
            &global_lane(),
            format!(
                "в этой сети не проходит {}: мертво на {} узлах — идём по запасному пути",
                axis.human_ru(),
                v.dead_nodes.len()
            ),
        ));
    }

    for lane in lanes {
        let st = state.get(&lane.id).unwrap_or(&NO_STATE);
        let empty: Vec<RouteId> = Vec::new();
        let cand_ids: &[RouteId] = candidates.get(&lane.id).unwrap_or(&empty);

        // Сглаженные серии обновляются ДО всякого решения и независимо от
        // него: измерение — не награда за прохождение ворот. Пропуск
        // обновления — это и есть тот самый мёртвый маршрут, который замер с
        // последней хорошей оценкой и выглядит лучше живого (раздел 5.5).
        // Поэтому обновляются все пригодные полосе маршруты и действующий —
        // и в аварии, и под закреплением, и когда кандидатов нет вовсе.
        refresh_series(lane, &warm(lane, cand_ids, snap, st), snap, damper, now, probe_interval_ms);

        // ── 1. АВАРИЙНЫЙ ПУТЬ. Обходит порог, выдержку и остывание ЦЕЛИКОМ.
        // Срабатывает по ФАКТАМ, а не по числам: оценка не имеет права
        // удержать человека на мёртвом маршруте.
        if let Some(cur) = st.current.clone() {
            if let Some(fact) = snap.health(&cur).and_then(emergency_fact) {
                match pick_emergency(
                    lane,
                    cand_ids,
                    snap,
                    damper,
                    now,
                    probe_interval_ms,
                    &cur,
                ) {
                    Some(next) => {
                        let same_axis = snap.route(&next).map(|r| r.axis)
                            == snap.route(&cur).map(|r| r.axis);
                        let tail = if same_axis {
                            format!("идём через {}", place(snap, &next, Case::Acc))
                        } else {
                            format!(
                                "идём через {} — {}",
                                place(snap, &next, Case::Acc),
                                snap.route(&next).map(|r| r.axis).unwrap_or(Axis::None).human_ru()
                            )
                        };
                        let reason = DecisionReason::new(
                            ReasonKind::EmergencyFact,
                            &lane.id,
                            format!(
                                "ушли с {}: {}; {}",
                                place(snap, &cur, Case::Gen),
                                fact.human_ru(),
                                tail
                            ),
                        );
                        reasons.push(reason.clone());
                        acts.push(Actuation::SelectLane {
                            lane: lane.id.clone(),
                            route: next.clone(),
                            reason,
                        });
                        // Аварийное переключение ВСЕГДА рвёт старые потоки:
                        // они уже текут по мёртвому или скомпрометированному
                        // пути, и дать им дотечь — значит дать им дотечь
                        // мимо туннеля.
                        acts.push(Actuation::Drain { lane: lane.id.clone() });
                        damper.on_switch(&lane.id, &next, now);
                    }
                    None => {
                        let reason = DecisionReason::new(
                            ReasonKind::NoCandidate,
                            &lane.id,
                            format!(
                                "ушли с {}: {}; заменить нечем — {}",
                                place(snap, &cur, Case::Gen),
                                fact.human_ru(),
                                on_empty_phrase(lane)
                            ),
                        );
                        reasons.push(reason.clone());
                        acts.push(Actuation::GoEmpty {
                            lane: lane.id.clone(),
                            action: lane.on_empty.clone(),
                            reason,
                        });
                    }
                }
                continue;
            }
        }

        // ── 2. Закрепление человеком сильнее всей математики — но не
        // сильнее фактов: до этой строки доходят только полосы, у которых
        // аварии нет.
        if let Some((pinned, until)) = st.pin.clone() {
            if until > now {
                // Мёртвое закрепление не спасает. Если этого не проверить,
                // получится качели: авария уводит с закреплённого маршрута,
                // а следующий же цикл возвращает обратно, потому что «так
                // просил человек».
                let dead = snap
                    .health(&pinned)
                    .map_or(true, |h| emergency_fact(h).is_some());
                // Закрепление сильнее МАТЕМАТИКИ, но не сильнее политики:
                // выбор человека не может открыть периметр полосы. Иначе
                // «закрепить локацию» стало бы способом выпустить трафик
                // открытым мимо всех проверок — ровно та дыра, ради которой
                // из типа `OnEmpty` убран прямой выход.
                let forbidden = !eligible(lane, snap, &pinned);
                if dead || forbidden {
                    let text = if forbidden {
                        format!(
                            "закрепление на {} не принято: маршрут не отвечает требованиям полосы «{}»",
                            place(snap, &pinned, Case::Acc),
                            lane.title
                        )
                    } else {
                        format!(
                            "закрепление на {} снято: маршрут не отвечает — выбираем сами",
                            place(snap, &pinned, Case::Acc)
                        )
                    };
                    let kind =
                        if forbidden { ReasonKind::UserPinned } else { ReasonKind::EmergencyFact };
                    reasons.push(DecisionReason::new(kind, &lane.id, text));
                } else {
                    if st.current.as_ref() != Some(&pinned) {
                        let reason = DecisionReason::new(
                            ReasonKind::UserPinned,
                            &lane.id,
                            format!(
                                "полоса «{}» закреплена человеком на {}",
                                lane.title,
                                place(snap, &pinned, Case::Acc)
                            ),
                        );
                        reasons.push(reason.clone());
                        acts.push(Actuation::SelectLane {
                            lane: lane.id.clone(),
                            route: pinned.clone(),
                            reason,
                        });
                        // on_switch здесь НЕ вызывается: штраф за флап
                        // вешать на человеческий выбор нельзя, а остывание
                        // — это защита от нашей же автоматики, не от
                        // человека.
                    } else {
                        reasons.push(DecisionReason::new(
                            ReasonKind::UserPinned,
                            &lane.id,
                            format!(
                                "держим {}: полоса «{}» закреплена человеком, автоматика не вмешивается",
                                place(snap, &pinned, Case::Acc),
                                lane.title
                            ),
                        ));
                    }
                    continue;
                }
            }
        }

        // ── 3 и 4. Отбор кандидатов и инвариант «последний живой».
        let q = qualify(lane, cand_ids, snap, damper, now, probe_interval_ms);
        if q.damper_overridden {
            reasons.push(DecisionReason::new(
                ReasonKind::DamperOverridden,
                &lane.id,
                format!(
                    "демпфер отставил все живые маршруты полосы «{}» — возвращаем их в игру: \
                     отказ в связи хуже дребезга",
                    lane.title
                ),
            ));
        } else if !q.suppressed.is_empty() {
            reasons.push(DecisionReason::new(
                ReasonKind::Suppressed,
                &lane.id,
                format!(
                    "отставлены за дребезг: {}",
                    q.suppressed
                        .iter()
                        .map(|r| place(snap, r, Case::Nom))
                        .collect::<Vec<_>>()
                        .join(", ")
                ),
            ));
        }

        if q.ready.is_empty() {
            let reason = DecisionReason::new(
                ReasonKind::NoCandidate,
                &lane.id,
                on_empty_phrase(lane),
            );
            reasons.push(reason.clone());
            acts.push(Actuation::GoEmpty {
                lane: lane.id.clone(),
                action: lane.on_empty.clone(),
                reason,
            });
            continue;
        }

        // ── 5. Оценка под КЛАСС ЭТОЙ полосы, сглаженная, минус осевой штраф.
        // Серии уже обновлены выше; здесь только читаем и ранжируем.
        let scored = ranked(lane, &q.ready, snap, damper, now, probe_interval_ms);

        // ── 6. ДИВЕРСИФИКАЦИЯ: горячий резерв обязан быть на ДРУГОЙ оси.
        // Само действие резерв не порождает — это подсказка измерительному
        // слою, какой маршрут держать прогретым (см. [`hot_standby`]).
        // Отсутствие резерва — факт, о котором человеку говорят: полоса без
        // запаса на другой оси не имеет запаса вообще.
        if lane.min_axes > 1 && first_on_other_axis(&scored, snap).is_none() {
            let lead_axis = snap
                .route(&scored[0].0)
                .map(|r| r.axis)
                .unwrap_or(Axis::None);
            reasons.push(DecisionReason::new(
                ReasonKind::NoCandidate,
                &lane.id,
                format!(
                    "у полосы «{}» нет запаса на другой оси: все живые маршруты стоят на «{}»",
                    lane.title,
                    lead_axis.human_ru()
                ),
            ));
        }

        let (best, best_s) = scored[0].clone();

        // Держаться не за что — включаем лучшее и на этом всё.
        let Some(cur) = st.current.clone() else {
            let reason = DecisionReason::new(
                ReasonKind::Initial,
                &lane.id,
                format!(
                    "включаем {}: первый выбор для полосы «{}»",
                    place(snap, &best, Case::Acc),
                    lane.title
                ),
            );
            reasons.push(reason.clone());
            acts.push(Actuation::SelectLane {
                lane: lane.id.clone(),
                route: best.clone(),
                reason,
            });
            damper.on_switch(&lane.id, &best, now);
            continue;
        };

        if best == cur {
            damper.reset_dwell(&lane.id);
            continue;
        }

        // Текущий маршрут мог исчезнуть из каталога или перестать
        // удовлетворять полосе (например, полоса запретила его страну).
        // Держаться за него в этом случае нельзя ни одной секунды: это не
        // вопрос качества, а вопрос политики и периметра.
        let cur_gone = snap.route(&cur).is_none() || !eligible(lane, snap, &cur);
        if cur_gone {
            let reason = DecisionReason::new(
                ReasonKind::Better,
                &lane.id,
                format!(
                    "прежний маршрут {} больше не подходит полосе «{}» — переходим на {}",
                    cur,
                    lane.title,
                    place(snap, &best, Case::Acc)
                ),
            );
            reasons.push(reason.clone());
            acts.push(Actuation::SelectLane {
                lane: lane.id.clone(),
                route: best.clone(),
                reason,
            });
            if lane.switch == SwitchMode::Cut {
                acts.push(Actuation::Drain { lane: lane.id.clone() });
            }
            damper.on_switch(&lane.id, &best, now);
            continue;
        }

        // ── 7. ГИСТЕРЕЗИС: A3 + бонус инкумбенту + шумовой пол порога.
        let cur_key = series_key(&lane.id, &cur);
        let cur_s = ranked(lane, std::slice::from_ref(&cur), snap, damper, now, probe_interval_ms)
            .first()
            .map(|x| x.1)
            .unwrap_or(f32::NEG_INFINITY);
        let margin = damper.margin(&cur_key, &lane.hysteresis);
        let sticky = damper
            .switched_at(&lane.id)
            .map_or(0.0, |t| damper::incumbent_bonus(t, now));

        if !damper::should_enter(best_s, cur_s, margin, sticky, HYS_POINTS) {
            // Зеркальная половина A3. Между входом и выходом лежит мёртвая
            // зона шириной 2·Hys: попав в неё, счётчик выдержки не
            // сбрасывается и не растёт. Без неё кандидат, колеблющийся
            // вокруг порога, обнулял бы отсчёт на каждом втором замере, и
            // переключение не состоялось бы никогда.
            if damper::should_exit(best_s, cur_s, margin, sticky, HYS_POINTS) {
                damper.reset_dwell(&lane.id);
                if best_s > cur_s {
                    // Кандидат впереди, но до порога не дотянул. Это тоже
                    // решение, и человеку оно интереснее прочих: именно так
                    // выглядит «почему не переключаетесь».
                    reasons.push(DecisionReason::new(
                        ReasonKind::Better,
                        &lane.id,
                        format!(
                            "держим {}: {} лучше на {:.1} — при пороге {:.1} это не стоит разрыва сессий",
                            place(snap, &cur, Case::Acc),
                            place(snap, &best, Case::Nom),
                            best_s - cur_s,
                            margin + sticky
                        ),
                    ));
                }
            }
            continue;
        }

        // Аналог Time-To-Trigger: условие обязано держаться подряд.
        if damper.bump_dwell(&lane.id) < lane.hysteresis.dwell {
            continue;
        }
        // Остывание: если предыдущее переключение не помогло, следующее тем
        // более не поможет, а стоит столько же.
        if let Some(switched_at) = damper.switched_at(&lane.id) {
            if now.since(switched_at) < damper.cooldown_ms(&lane.id, &lane.hysteresis) {
                continue;
            }
        }

        // ── 8. ПЕРЕКЛЮЧЕНИЕ.
        let reason = DecisionReason::new(
            ReasonKind::Better,
            &lane.id,
            format!(
                "перешли с {} на {}: {:.0} против {:.0} пунктов при пороге {:.0}",
                place(snap, &cur, Case::Gen),
                place(snap, &best, Case::Acc),
                best_s,
                cur_s,
                margin + sticky
            ),
        );
        reasons.push(reason.clone());
        acts.push(Actuation::SelectLane {
            lane: lane.id.clone(),
            route: best.clone(),
            reason,
        });
        // Обрыв потоков — решение политики. Переключение селектора само по
        // себе их не рвёт, поэтому «дотечь» бесплатно, а «оборвать» надо
        // просить явно.
        if lane.switch == SwitchMode::Cut {
            acts.push(Actuation::Drain { lane: lane.id.clone() });
        }
        // on_switch сам обнуляет выдержку и начисляет штраф за флап тому,
        // в кого вошли.
        damper.on_switch(&lane.id, &best, now);
    }

    (acts, reasons)
}

// ──────────────────────────────── Тесты ────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::axis::{Exposure, ExposureSet};
    use crate::ids::TransportId;
    use crate::lane::{Hysteresis, RouteRequirements};
    use crate::metrics::{Probe, ProbeOutcome};
    use crate::route::{Carries, HandshakeCost};
    use crate::score::SlaClass;

    const ШАГ: u64 = 5_000;
    /// Столько проб нужно, чтобы уверенность перевалила за порог участия в
    /// выборе. Меньше — и маршрут отсеется не по качеству, а по незнанию.
    const ПРОБ: u32 = 150;

    fn сейчас() -> Instant {
        Instant((ПРОБ as u64 - 1) * ШАГ)
    }

    fn маршрут(id: &str, node: &str, axis: Axis) -> Route {
        Route {
            id: RouteId::new(id),
            node: NodeId::new(node),
            transport: TransportId::new("t"),
            axis,
            exposure: Exposure::Tunnelled { node: NodeId::new(node) },
            carries: Carries::default(),
            handshake_cost: HandshakeCost::Cheap,
        }
    }

    /// Здоровье с постоянной задержкой. Постоянная — чтобы разница между
    /// маршрутами в тесте бралась ровно оттуда, откуда задумано.
    fn живое(id: &str, axis: Axis, rtt: f32) -> RouteHealth {
        let mut h = RouteHealth::new(RouteId::new(id), axis);
        for i in 0..ПРОБ {
            h.observe(&Probe {
                route: RouteId::new(id),
                at: Instant(i as u64 * ШАГ),
                outcome: ProbeOutcome::Ok { rtt_ms: rtt },
            });
        }
        h
    }

    /// То же, но последние три пробы — без ответа. Заканчивается в тот же
    /// момент, что и живое: иначе разница возраста сама по себе уронила бы
    /// уверенность и тест доказывал бы не то.
    fn мёртвое(id: &str, axis: Axis, rtt: f32) -> RouteHealth {
        let mut h = RouteHealth::new(RouteId::new(id), axis);
        for i in 0..ПРОБ {
            let outcome = if i >= ПРОБ - 3 {
                ProbeOutcome::Timeout
            } else {
                ProbeOutcome::Ok { rtt_ms: rtt }
            };
            h.observe(&Probe { route: RouteId::new(id), at: Instant(i as u64 * ШАГ), outcome });
        }
        h
    }

    fn полоса(id: &str, on_empty: OnEmpty) -> Lane {
        Lane {
            id: LaneId::new(id),
            title: "Веб".into(),
            sla: SlaClass::Browse,
            allow: ExposureSet::TUNNELLED,
            justification: None,
            need: RouteRequirements::default(),
            min_axes: 2,
            on_empty,
            switch: SwitchMode::Drain,
            hysteresis: Hysteresis::default(),
        }
    }

    struct Мир {
        snap: Snapshot,
        candidates: BTreeMap<LaneId, Vec<RouteId>>,
        state: BTreeMap<LaneId, LaneState>,
        damper: DamperState,
    }

    impl Мир {
        fn new() -> Self {
            Self {
                snap: Snapshot::new(),
                candidates: BTreeMap::new(),
                state: BTreeMap::new(),
                damper: DamperState::new(),
            }
        }

        fn добавить(&mut self, lane: &Lane, r: Route, h: RouteHealth, country: &str) {
            self.candidates.entry(lane.id.clone()).or_default().push(r.id.clone());
            self.snap.insert(r, h, country.into());
        }

        fn текущий(&mut self, lane: &Lane, route: &str) {
            self.state.entry(lane.id.clone()).or_default().current = Some(RouteId::new(route));
        }

        fn свести(&mut self, lanes: &[Lane], now: Instant) -> (Vec<Actuation>, Vec<DecisionReason>) {
            reconcile(
                lanes,
                &self.candidates,
                &self.snap,
                &self.state,
                &mut self.damper,
                now,
                ШАГ,
            )
        }
    }

    fn выбран(acts: &[Actuation]) -> Option<RouteId> {
        acts.iter().find_map(|a| match a {
            Actuation::SelectLane { route, .. } => Some(route.clone()),
            _ => None,
        })
    }

    fn есть_обрыв(acts: &[Actuation]) -> bool {
        acts.iter().any(|a| matches!(a, Actuation::Drain { .. }))
    }

    fn род(reasons: &[DecisionReason], kind: ReasonKind) -> bool {
        reasons.iter().any(|r| r.kind == kind)
    }

    // ── 1. Факты сильнее чисел ──

    #[test]
    fn мертвый_активный_маршрут_переключает_раньше_остывания() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.hy2", "lt", Axis::QuicUdp), мёртвое("lt.hy2", Axis::QuicUdp, 40.0), "LT");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 60.0), "FI");
        w.текущий(&lane, "lt.hy2");

        // Полосу только что переключали: остывание запрещает трогать её
        // ещё 60 секунд.
        let недавно = Instant(сейчас().0 - 10_000);
        w.damper.on_switch(&lane.id, &RouteId::new("lt.hy2"), недавно);
        assert!(
            w.damper.cooldown_ms(&lane.id, &lane.hysteresis) > 10_000,
            "тест бессмыслен, если остывание уже истекло"
        );

        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(выбран(&acts), Some(RouteId::new("fi.trojan")), "остались на мёртвом маршруте");
        assert!(есть_обрыв(&acts), "аварийное переключение обязано рвать старые потоки");
        assert!(род(&reasons, ReasonKind::EmergencyFact));
    }

    #[test]
    fn живой_но_худший_маршрут_остыванием_как_раз_удерживается() {
        // Контрольный опыт к предыдущему тесту: без факта смерти то же
        // остывание переключиться НЕ даёт. Иначе первый тест доказывал бы
        // лишь то, что остывание вообще не работает.
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.hy2", "lt", Axis::QuicUdp), живое("lt.hy2", Axis::QuicUdp, 250.0), "LT");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 30.0), "FI");
        w.текущий(&lane, "lt.hy2");

        let недавно = Instant(сейчас().0 - 10_000);
        w.damper.on_switch(&lane.id, &RouteId::new("lt.hy2"), недавно);

        // Предпосылка: замена существует, дошла до сравнения и выигрывает —
        // иначе тест доказывал бы лишь то, что кандидатов нет.
        let ids = w.candidates.get(&lane.id).unwrap().clone();
        let свежий = DamperState::new();
        let q = qualify(&lane, &ids, &w.snap, &свежий, сейчас(), ШАГ);
        assert!(q.ready.contains(&RouteId::new("fi.trojan")));
        let r0 = ranked(&lane, &q.ready, &w.snap, &свежий, сейчас(), ШАГ);
        assert_eq!(r0[0].0, RouteId::new("fi.trojan"), "замена не лучше текущего");

        // Выдержка в три замера + остывание: даже за три цикла переключения
        // не будет.
        for i in 0..3 {
            let (acts, _) = w.свести(std::slice::from_ref(&lane), Instant(сейчас().0 + i * 10));
            assert_eq!(выбран(&acts), None, "остывание не удержало на цикле {i}");
        }
    }

    // ── 2. Виновата бывает сеть, а не сервер ──

    #[test]
    fn смерть_оси_на_двух_узлах_штрафует_ось_а_не_узлы() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        // QUIC мёртв на двух узлах, но жив на третьем — и он объективно
        // лучший из всех по измерениям.
        w.добавить(&lane, маршрут("lt.hy2", "lt", Axis::QuicUdp), мёртвое("lt.hy2", Axis::QuicUdp, 40.0), "LT");
        w.добавить(&lane, маршрут("us.hy2", "us", Axis::QuicUdp), мёртвое("us.hy2", Axis::QuicUdp, 40.0), "US");
        w.добавить(&lane, маршрут("fi.hy2", "fi", Axis::QuicUdp), живое("fi.hy2", Axis::QuicUdp, 30.0), "FI");
        // TCP-ось жива — именно этим фильтрация QUIC отличается от «нет сети».
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 120.0), "LT");
        w.добавить(&lane, маршрут("us.trojan", "us", Axis::RealTls), живое("us.trojan", Axis::RealTls, 150.0), "US");

        // Предпосылка: по чистым измерениям выигрывает финский QUIC — он
        // доходит до сравнения и стоит в ранжировании первым. Без этой
        // проверки тест прошёл бы и в случае, когда финский QUIC отсеян
        // чем-то посторонним, то есть доказывал бы не то.
        let ids = w.candidates.get(&lane.id).unwrap().clone();
        let свежий = DamperState::new();
        let q = qualify(&lane, &ids, &w.snap, &свежий, сейчас(), ШАГ);
        assert!(
            q.ready.contains(&RouteId::new("fi.hy2")),
            "живой финский QUIC не дошёл до сравнения: {:?}",
            q.ready
        );
        let r0 = ranked(&lane, &q.ready, &w.snap, &свежий, сейчас(), ШАГ);
        assert_eq!(
            r0[0].0,
            RouteId::new("fi.hy2"),
            "без осевого вердикта выбор был бы другим: {r0:?}"
        );

        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());

        assert!(род(&reasons, ReasonKind::AxisDead));
        assert!(
            w.damper.axis_penalty_points(Axis::QuicUdp, сейчас()) > 0.0,
            "ось не оштрафована"
        );
        // Узлы и маршруты не наказаны: виновата сеть.
        for r in ["lt.hy2", "us.hy2", "fi.hy2"] {
            assert_eq!(
                w.damper.penalty(&RouteId::new(r), сейчас()),
                0.0,
                "оштрафован маршрут {r}, хотя виновата сеть"
            );
        }
        // Живой маршрут той же оси на ТРЕТЬЕМ узле понижен вместе с осью и
        // проиграл объективно худшему TCP-маршруту.
        assert_eq!(
            выбран(&acts),
            Some(RouteId::new("lt.trojan")),
            "финский QUIC выбран, хотя вся ось не проходит"
        );
    }

    #[test]
    fn смерть_одной_оси_на_одном_узле_осью_не_считается() {
        // Один узел — это поломка узла, а не работа сети. Штрафовать ось
        // по одному свидетельству нельзя: так теряется единственный
        // работающий способ пройти.
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.hy2", "lt", Axis::QuicUdp), мёртвое("lt.hy2", Axis::QuicUdp, 40.0), "LT");
        w.добавить(&lane, маршрут("fi.hy2", "fi", Axis::QuicUdp), живое("fi.hy2", Axis::QuicUdp, 30.0), "FI");
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 120.0), "LT");

        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert!(!род(&reasons, ReasonKind::AxisDead));
        assert_eq!(w.damper.axis_penalty_points(Axis::QuicUdp, сейчас()), 0.0);
        assert_eq!(выбран(&acts), Some(RouteId::new("fi.hy2")));
    }

    // ── 3. Человек сильнее математики, но не сильнее фактов ──

    #[test]
    fn закрепление_переживает_лучшее_предложение_математики() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 250.0), "LT");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 30.0), "FI");
        w.текущий(&lane, "lt.trojan");
        w.state.get_mut(&lane.id).unwrap().pin =
            Some((RouteId::new("lt.trojan"), Instant(сейчас().0 + 3_600_000)));

        // Предпосылка: без закрепления математика ушла бы в Финляндию.
        let ids = w.candidates.get(&lane.id).unwrap().clone();
        let свежий = DamperState::new();
        let q = qualify(&lane, &ids, &w.snap, &свежий, сейчас(), ШАГ);
        let r0 = ranked(&lane, &q.ready, &w.snap, &свежий, сейчас(), ШАГ);
        assert_eq!(r0[0].0, RouteId::new("fi.trojan"), "спорить не с чем: {r0:?}");

        // Даже если условие держится сколько угодно циклов подряд.
        for _ in 0..5 {
            let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
            assert_eq!(выбран(&acts), None, "закрепление не удержало выбор человека");
            assert!(род(&reasons, ReasonKind::UserPinned));
        }
    }

    #[test]
    fn закрепление_не_переживает_смерть_закрепленного_маршрута() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), мёртвое("lt.trojan", Axis::RealTls, 40.0), "LT");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 60.0), "FI");
        w.текущий(&lane, "lt.trojan");
        w.state.get_mut(&lane.id).unwrap().pin =
            Some((RouteId::new("lt.trojan"), Instant(сейчас().0 + 3_600_000)));

        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(выбран(&acts), Some(RouteId::new("fi.trojan")));
        assert!(род(&reasons, ReasonKind::EmergencyFact));

        // И обратно не возвращает: исполнитель применил решение, полоса уже
        // на живом маршруте, а закрепление всё ещё указывает на мёртвый.
        w.текущий(&lane, "fi.trojan");
        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_ne!(
            выбран(&acts),
            Some(RouteId::new("lt.trojan")),
            "закрепление вернуло полосу на мёртвый маршрут — те самые качели"
        );
        assert!(
            reasons.iter().any(|r| r.human_ru.contains("закрепление") && r.kind == ReasonKind::EmergencyFact),
            "снятие закрепления не объяснено человеку: {reasons:?}"
        );
    }

    #[test]
    fn закрепление_не_открывает_периметр_полосы() {
        // Полоса пускает только туннель, а человек закрепил прямой выход.
        // Закрепление сильнее математики, но не сильнее политики.
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        let mut direct = маршрут("direct", "-", Axis::None);
        direct.exposure = Exposure::Direct;
        w.добавить(&lane, direct, живое("direct", Axis::None, 5.0), "RU");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 120.0), "FI");
        w.state.entry(lane.id.clone()).or_default().pin =
            Some((RouteId::new("direct"), Instant(сейчас().0 + 3_600_000)));

        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_ne!(выбран(&acts), Some(RouteId::new("direct")), "закрепление открыло периметр");
        assert_eq!(выбран(&acts), Some(RouteId::new("fi.trojan")));
        assert!(
            reasons.iter().any(|r| r.human_ru.contains("не принято")),
            "отказ выполнить закрепление не объяснён человеку: {reasons:?}"
        );
    }

    // ── 4. Пустота ──

    #[test]
    fn без_живых_кандидатов_полоса_уходит_в_блок_а_не_в_прежний_маршрут() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.hy2", "lt", Axis::QuicUdp), мёртвое("lt.hy2", Axis::QuicUdp, 40.0), "LT");
        w.добавить(&lane, маршрут("fi.hy2", "fi", Axis::QuicUdp), мёртвое("fi.hy2", Axis::QuicUdp, 40.0), "FI");
        w.текущий(&lane, "lt.hy2");

        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(выбран(&acts), None, "выбран маршрут, хотя живых нет");
        let empty = acts.iter().find_map(|a| match a {
            Actuation::GoEmpty { action, .. } => Some(action.clone()),
            _ => None,
        });
        assert_eq!(empty, Some(OnEmpty::Block), "полоса не ушла в своё действие пустоты");
        assert!(род(&reasons, ReasonKind::NoCandidate));
    }

    #[test]
    fn прямой_выход_не_подставляется_даже_когда_он_лучший() {
        // Полоса допускает только туннель. Прямой выход быстрее любого
        // туннеля по определению — и потому обязан быть исключён ДО оценки.
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        let mut direct = маршрут("direct", "-", Axis::None);
        direct.exposure = Exposure::Direct;
        w.добавить(&lane, direct, живое("direct", Axis::None, 5.0), "RU");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 120.0), "FI");

        // Предпосылка: по измерениям прямой выход лучший — отсекает его
        // именно замыкание экспозиции, а не оценка.
        let s_d = score::score(w.snap.health(&RouteId::new("direct")).unwrap(), lane.sla, сейчас(), ШАГ);
        let s_f = score::score(w.snap.health(&RouteId::new("fi.trojan")).unwrap(), lane.sla, сейчас(), ШАГ);
        assert!(s_d.value > s_f.value && s_d.confidence >= CONF_FLOOR);

        let (acts, _) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(выбран(&acts), Some(RouteId::new("fi.trojan")));

        // А когда прямой выход остаётся единственным — полоса уходит в блок,
        // а не «хотя бы куда-нибудь».
        let mut w2 = Мир::new();
        let mut direct = маршрут("direct", "-", Axis::None);
        direct.exposure = Exposure::Direct;
        w2.добавить(&lane, direct, живое("direct", Axis::None, 5.0), "RU");
        let (acts, _) = w2.свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(выбран(&acts), None, "трафик выпущен открытым — это дыра, а не запасной путь");
        assert!(acts.iter().any(|a| matches!(
            a,
            Actuation::GoEmpty { action: OnEmpty::Block, .. }
        )));
    }

    // ── 5. Демпфер не имеет права на отказ в обслуживании ──

    #[test]
    fn подавленный_маршрут_возвращается_если_он_последний_живой() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 60.0), "LT");
        // Семь флапов подряд — выше порога подавления 6000.
        for _ in 0..7 {
            w.damper.penalize_flap(&RouteId::new("lt.trojan"), сейчас());
        }
        assert!(w.damper.is_suppressed(&RouteId::new("lt.trojan"), сейчас()));

        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(выбран(&acts), Some(RouteId::new("lt.trojan")), "демпфер оставил полосу вообще без связи");
        assert!(род(&reasons, ReasonKind::DamperOverridden));
    }

    #[test]
    fn подавленный_маршрут_уступает_живому_когда_живой_есть() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        // Подавленный при этом объективно лучший — проверяем, что снятие
        // подавления не превратилось в «подавления нет».
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 30.0), "LT");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 120.0), "FI");
        for _ in 0..7 {
            w.damper.penalize_flap(&RouteId::new("lt.trojan"), сейчас());
        }

        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(выбран(&acts), Some(RouteId::new("fi.trojan")));
        assert!(род(&reasons, ReasonKind::Suppressed));
    }

    // ── 6. Диверсификация ──

    #[test]
    fn горячий_резерв_всегда_на_другой_оси() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        // Две лучшие оценки — на одной оси. Резервом обязан стать третий.
        w.добавить(&lane, маршрут("fi.hy2", "fi", Axis::QuicUdp), живое("fi.hy2", Axis::QuicUdp, 30.0), "FI");
        w.добавить(&lane, маршрут("lt.hy2", "lt", Axis::QuicUdp), живое("lt.hy2", Axis::QuicUdp, 60.0), "LT");
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 120.0), "LT");

        let ids = w.candidates.get(&lane.id).unwrap().clone();
        let standby = hot_standby(&lane, &ids, &w.snap, &w.damper, сейчас(), ШАГ);
        assert_eq!(
            standby,
            Some(RouteId::new("lt.trojan")),
            "резервом взят маршрут той же оси — его убьёт то же самое"
        );

        // Когда другой оси нет вовсе — резерва нет, и об этом говорят вслух.
        let mut w2 = Мир::new();
        w2.добавить(&lane, маршрут("fi.hy2", "fi", Axis::QuicUdp), живое("fi.hy2", Axis::QuicUdp, 30.0), "FI");
        w2.добавить(&lane, маршрут("lt.hy2", "lt", Axis::QuicUdp), живое("lt.hy2", Axis::QuicUdp, 60.0), "LT");
        let ids2 = w2.candidates.get(&lane.id).unwrap().clone();
        assert_eq!(hot_standby(&lane, &ids2, &w2.snap, &w2.damper, сейчас(), ШАГ), None);
        let (_, reasons) = w2.свести(std::slice::from_ref(&lane), сейчас());
        assert!(
            reasons.iter().any(|r| r.human_ru.contains("нет запаса на другой оси")),
            "полоса без запаса на другой оси промолчала: {reasons:?}"
        );
    }

    // ── 7. Гистерезис ──

    #[test]
    fn мелкое_улучшение_не_стоит_разрыва_сессий() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        // Разница задержек мала: оценки расходятся на считаные пункты.
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 90.0), "LT");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 80.0), "FI");
        w.текущий(&lane, "lt.trojan");

        for i in 0..10 {
            let (acts, _) = w.свести(std::slice::from_ref(&lane), Instant(сейчас().0 + i * 10));
            assert_eq!(выбран(&acts), None, "переключились ради нескольких пунктов");
        }
        let (_, reasons) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert!(
            reasons.iter().any(|r| r.human_ru.starts_with("держим")),
            "решение остаться не объяснено человеку: {reasons:?}"
        );
    }

    #[test]
    fn крупное_улучшение_переключает_но_не_раньше_выдержки() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 250.0), "LT");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 30.0), "FI");
        w.текущий(&lane, "lt.trojan");

        // Полосу никогда не переключали: остывание равно нулю, мешает
        // только выдержка в три замера.
        let dwell = lane.hysteresis.dwell as u64;
        for i in 0..dwell - 1 {
            let (acts, _) = w.свести(std::slice::from_ref(&lane), Instant(сейчас().0 + i * 10));
            assert_eq!(выбран(&acts), None, "переключились раньше выдержки, на замере {i}");
        }
        let (acts, reasons) = w.свести(std::slice::from_ref(&lane), Instant(сейчас().0 + dwell * 10));
        assert_eq!(выбран(&acts), Some(RouteId::new("fi.trojan")));
        assert!(род(&reasons, ReasonKind::Better));
        assert!(!есть_обрыв(&acts), "обычное переключение не должно рвать соединения");
    }

    #[test]
    fn режим_cut_рвет_соединения_а_drain_нет() {
        let mut lane = полоса("corp", OnEmpty::Block);
        lane.switch = SwitchMode::Cut;
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 250.0), "LT");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 30.0), "FI");
        w.текущий(&lane, "lt.trojan");

        let mut acts = Vec::new();
        for i in 0..=lane.hysteresis.dwell as u64 {
            acts = w.свести(std::slice::from_ref(&lane), Instant(сейчас().0 + i * 10)).0;
            if выбран(&acts).is_some() {
                break;
            }
        }
        assert_eq!(выбран(&acts), Some(RouteId::new("fi.trojan")));
        assert!(есть_обрыв(&acts), "полоса с Cut обязана оборвать старые потоки");
    }

    // ── 8. Требования полосы ──

    #[test]
    fn требования_полосы_отсекают_маршрут_до_оценки() {
        let mut lane = полоса("call", OnEmpty::Block);
        lane.need = RouteRequirements { exclude_country: vec!["US".into()], ..Default::default() };
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("us.trojan", "us", Axis::RealTls), живое("us.trojan", Axis::RealTls, 20.0), "US");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 120.0), "FI");

        let (acts, _) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(выбран(&acts), Some(RouteId::new("fi.trojan")));
    }

    #[test]
    fn инвариант_последнего_живого_не_отменяет_требований_полосы() {
        // Снятие подавления возвращает в игру подавленных, но НЕ тех, кто
        // полосе запрещён: иначе демпфер стал бы обходным путём мимо
        // политики.
        let mut lane = полоса("call", OnEmpty::Block);
        lane.need = RouteRequirements { exclude_country: vec!["US".into()], ..Default::default() };
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("us.trojan", "us", Axis::RealTls), живое("us.trojan", Axis::RealTls, 20.0), "US");
        w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 120.0), "FI");
        for _ in 0..7 {
            w.damper.penalize_flap(&RouteId::new("fi.trojan"), сейчас());
        }

        let (acts, _) = w.свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(
            выбран(&acts),
            Some(RouteId::new("fi.trojan")),
            "вместо возврата подавленного взят запрещённый полосе маршрут"
        );
    }

    // ── 9. Чистота ──

    #[test]
    fn решение_воспроизводимо_на_одном_и_том_же_входе() {
        // Прогон на записанном логе обязан давать тот же ответ — иначе
        // гистерезис настраивается вслепую.
        let lane = полоса("web", OnEmpty::Block);
        let построить = || {
            let mut w = Мир::new();
            w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 90.0), "LT");
            w.добавить(&lane, маршрут("fi.trojan", "fi", Axis::RealTls), живое("fi.trojan", Axis::RealTls, 90.0), "FI");
            w.добавить(&lane, маршрут("us.hy2", "us", Axis::QuicUdp), живое("us.hy2", Axis::QuicUdp, 90.0), "US");
            w
        };
        let (a1, r1) = построить().свести(std::slice::from_ref(&lane), сейчас());
        let (a2, r2) = построить().свести(std::slice::from_ref(&lane), сейчас());
        assert_eq!(a1, a2);
        assert_eq!(r1, r2);
    }

    #[test]
    fn серии_оценок_у_полос_разных_классов_не_смешиваются() {
        // Один маршрут в двух полосах разных классов ведёт ДВЕ серии:
        // общая давала бы среднее, не равное ни одной из оценок, и порог
        // считался бы по разнице классов вместо шума сети.
        let web = полоса("web", OnEmpty::Block);
        let mut call = полоса("call", OnEmpty::Block);
        call.sla = SlaClass::Realtime;

        let mut w = Мир::new();
        // Рваная задержка: между классами оценка расходится сильно.
        let mut h = RouteHealth::new(RouteId::new("lt.trojan"), Axis::RealTls);
        for i in 0..ПРОБ {
            let rtt = if i % 2 == 0 { 30.0 } else { 110.0 };
            h.observe(&Probe {
                route: RouteId::new("lt.trojan"),
                at: Instant(i as u64 * ШАГ),
                outcome: ProbeOutcome::Ok { rtt_ms: rtt },
            });
        }
        w.candidates.insert(web.id.clone(), vec![RouteId::new("lt.trojan")]);
        w.candidates.insert(call.id.clone(), vec![RouteId::new("lt.trojan")]);
        w.snap.insert(маршрут("lt.trojan", "lt", Axis::RealTls), h, "LT".into());

        let lanes = vec![web.clone(), call.clone()];
        w.свести(&lanes, сейчас());

        let s_web = w.damper.smoothed_of(&series_key(&web.id, &RouteId::new("lt.trojan")));
        let s_call = w.damper.smoothed_of(&series_key(&call.id, &RouteId::new("lt.trojan")));
        assert!(s_web.is_some() && s_call.is_some());
        assert!(
            (s_web.unwrap() - s_call.unwrap()).abs() > 1.0,
            "оценки двух классов слились в одну: web={s_web:?}, call={s_call:?}"
        );
    }

    #[test]
    fn выбывший_по_воротам_маршрут_не_замирает_с_прошлой_хорошей_оценкой() {
        // Маршрут, выпавший из выбора, продолжает измеряться. Иначе он
        // вернётся с оценкой времён, когда был хорош, и выиграет сравнение
        // прошлыми заслугами — та же ошибка, что «мёртвый маршрут замер с
        // последней хорошей оценкой».
        let lane = полоса("web", OnEmpty::Block);
        let id = RouteId::new("lt.trojan");
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 30.0), "LT");
        w.свести(std::slice::from_ref(&lane), сейчас());
        let было = w.damper.smoothed_of(&series_key(&lane.id, &id)).unwrap();
        assert!(было > 90.0, "предпосылка: маршрут был хорош, а вышло {было}");

        // Ворота: маршрут начал подменять ответы DNS — из выбора он выбыл.
        let mut порченое = живое("lt.trojan", Axis::RealTls, 30.0);
        порченое.dns_tampered = true;
        w.snap.insert(маршрут("lt.trojan", "lt", Axis::RealTls), порченое, "LT".into());

        let (acts, _) = w.свести(std::slice::from_ref(&lane), Instant(сейчас().0 + 40_000));
        assert_eq!(выбран(&acts), None, "маршрут с подменой DNS выбран");
        let стало = w.damper.smoothed_of(&series_key(&lane.id, &id)).unwrap();
        assert!(
            стало < было - 10.0,
            "оценка замерла на прошлой хорошей: было {было}, стало {стало}"
        );
    }

    #[test]
    fn смена_сети_забывает_и_серии_полос() {
        let lane = полоса("web", OnEmpty::Block);
        let mut w = Мир::new();
        w.добавить(&lane, маршрут("lt.trojan", "lt", Axis::RealTls), живое("lt.trojan", Axis::RealTls, 60.0), "LT");
        w.свести(std::slice::from_ref(&lane), сейчас());
        let id = RouteId::new("lt.trojan");
        assert!(w.damper.smoothed_of(&series_key(&lane.id, &id)).is_some());

        forget_route_everywhere(&mut w.damper, std::slice::from_ref(&lane), &id);
        assert!(
            w.damper.smoothed_of(&series_key(&lane.id, &id)).is_none(),
            "серия полосы пережила смену сети — задержки через другой канал несравнимы"
        );
    }
}
