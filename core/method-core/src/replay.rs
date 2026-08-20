//! Прогон движка на ЗАПИСАННЫХ измерениях.
//!
//! Без этого модуля константы гистерезиса остаются мнением. Раздел 15
//! документа называет это риском Р9 прямым текстом: «веса проверены только
//! на синтетике», «если replay harness не появился к концу Э1, тюнинг
//! констант будет вслепую». Здесь и появляется единственный способ узнать,
//! сколько переключений движок выдаст на реальной сети, ДО того как он
//! попадёт на устройство человека.
//!
//! Модуль остаётся в тех же границах, что и всё ядро: ни сети, ни часов, ни
//! файлов. На вход — текст лога и политика, на выход — отчёт. Чтение файла
//! делает вызывающий (см. `examples/replay.rs`).
//!
//! ## Что именно прогоняется
//!
//! Полная цепочка, ровно та же, что и в живом клиенте:
//!
//! ```text
//! строка лога → Probe → RouteHealth::observe → score → reconcile → действия
//! ```
//!
//! Ни один шаг не подменён упрощением. Подменено только окружение: время
//! берётся из поля `ts` записи, а не из часов, и никаких проб никто не
//! шлёт — они уже посланы, когда лог снимали.
//!
//! ## Формат лога
//!
//! Одна строка — один JSON-объект, одно измерение одного маршрута в одном
//! раунде:
//!
//! ```json
//! {"ts": 1787221238.5, "round": 1, "name": "…-US · alt", "host": "92.…",
//!  "transport": "hysteria2", "self": false, "core_ok": true,
//!  "connect_ms": 728.5, "probes": [{"ms": 143.1, "ok": true}], "mbps": null}
//! ```
//!
//! Формат чужой: его пишет калибровочный сборщик на ноде, и он может
//! поменяться. Поэтому разбор здесь нарочно терпимый — незнакомые поля
//! игнорируются, строка без обязательных полей не роняет прогон, а
//! считается в `bad_lines`.

use std::collections::BTreeMap;

use serde_json::Value;

use crate::axis::{Axis, Exposure};
use crate::damper::DamperState;
use crate::decide::{self, Actuation, LaneState, ReasonKind, Snapshot};
use crate::ids::{LaneId, NodeId, RouteId, TransportId};
use crate::lane::{Lane, OnEmpty};
use crate::metrics::{Probe, ProbeOutcome, RouteHealth};
use crate::route::{Carries, HandshakeCost, Route};
use crate::score::{self, GateId};
use crate::Instant;

// ──────────────────────────── Разбор лога ────────────────────────────

/// Одна проба внутри записи.
#[derive(Clone, Debug)]
pub struct RecordedProbe {
    /// `None` — проба не состоялась. Сборщик в этом случае пишет `null`, а
    /// не ноль, и это важно: ноль означал бы мгновенный ответ.
    pub ms: Option<f32>,
    pub ok: bool,
}

/// Одна строка лога: измерение одного маршрута в одном раунде.
#[derive(Clone, Debug)]
pub struct Record {
    /// Момент по стенным часам сборщика, секунды. В `Instant` переводится
    /// вычитанием начала лога: ядру нужна монотонная разность, а не дата.
    pub ts_s: f64,
    pub round: u64,
    pub name: String,
    pub host: String,
    pub transport: String,
    /// Петля с самой ноды-измерителя: маршрут «в себя». В сравнение не
    /// идёт — это не путь пользователя, а замер локальной петли.
    pub is_self: bool,
    /// Поднялось ли ядро сборщика. `false` означает, что измерить НЕ
    /// УДАЛОСЬ, а не что маршрут плох.
    pub core_ok: bool,
    pub connect_ms: Option<f32>,
    pub mbps: Option<f32>,
    pub probes: Vec<RecordedProbe>,
}

impl Record {
    /// Контрольная строка прямого выхода: не маршрут, а точка отсчёта.
    pub fn is_control(&self) -> bool {
        self.transport == "direct"
    }
}

/// Разобранный лог.
#[derive(Clone, Debug, Default)]
pub struct Log {
    pub records: Vec<Record>,
    /// Строки, которые разобрать не удалось. Считаются, а не роняют прогон:
    /// лог пишется на живой ноде и может оборваться на середине строки.
    pub bad_lines: usize,
}

fn num(v: Option<&Value>) -> Option<f32> {
    v.and_then(Value::as_f64).map(|x| x as f32)
}

fn текст(v: Option<&Value>) -> Option<String> {
    v.and_then(Value::as_str).map(str::to_owned)
}

/// Разобрать JSONL.
///
/// Разбор идёт через `serde_json::Value`, а не через структуру с `derive`.
/// Причина: описывать чужой формат типом значит связать себя с ним. Стоит
/// сборщику добавить поле или переименовать своё — и `derive` начнёт
/// ронять строки, которые прекрасно читаются. Здесь берутся ровно те поля,
/// которые нужны движку, остальное проходит мимо.
pub fn parse_jsonl(text: &str) -> Log {
    let mut log = Log::default();
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        let Ok(v) = serde_json::from_str::<Value>(line) else {
            log.bad_lines += 1;
            continue;
        };
        let (Some(ts_s), Some(name), Some(transport)) = (
            v.get("ts").and_then(Value::as_f64),
            текст(v.get("name")),
            текст(v.get("transport")),
        ) else {
            log.bad_lines += 1;
            continue;
        };
        let probes = v
            .get("probes")
            .and_then(Value::as_array)
            .map(|arr| {
                arr.iter()
                    .map(|p| RecordedProbe {
                        ms: num(p.get("ms")),
                        // Отсутствие `ok` считаем неуспехом: молчание о
                        // результате — не свидетельство успеха.
                        ok: p.get("ok").and_then(Value::as_bool).unwrap_or(false),
                    })
                    .collect()
            })
            .unwrap_or_default();
        log.records.push(Record {
            ts_s,
            round: v.get("round").and_then(Value::as_u64).unwrap_or(0),
            name,
            host: текст(v.get("host")).unwrap_or_default(),
            transport,
            is_self: v.get("self").and_then(Value::as_bool).unwrap_or(false),
            core_ok: v.get("core_ok").and_then(Value::as_bool).unwrap_or(true),
            connect_ms: num(v.get("connect_ms")),
            mbps: num(v.get("mbps")),
            probes,
        });
    }
    log
}

// ──────────────────── Из записи — в маршрут ядра ────────────────────

/// Транспорт сборщика → ось обхода.
///
/// Таблица из §5.2 брифинга проекта. `None` означает «не знаем, что это» —
/// такой маршрут в прогон не идёт: приписать незнакомому транспорту ось
/// наугад значит соврать осевому вердикту, а он решает, обвинять сеть или
/// сервер.
pub fn axis_of_transport(t: &str) -> Option<Axis> {
    match t {
        "hysteria2" | "tuic" => Some(Axis::QuicUdp),
        "vless-grpc" | "grpc" => Some(Axis::FakeTlsH2),
        "vless-vision" | "vless" | "reality" => Some(Axis::FakeTlsTcp),
        "trojan" => Some(Axis::RealTls),
        "ss" | "shadowsocks" | "shadowsocks-2022" => Some(Axis::RawStream),
        _ => None,
    }
}

/// Дорого ли маршруту устанавливать соединение.
///
/// Влияет только на бюджет проб, а не на выбор, — но поле обязательное, и
/// врать в нём незачем: QUIC с 0-RTT и голый поток дёшевы, полное TLS-
/// рукопожатие нет.
fn handshake_cost(t: &str) -> HandshakeCost {
    match axis_of_transport(t) {
        Some(Axis::QuicUdp) | Some(Axis::RawStream) => HandshakeCost::Cheap,
        _ => HandshakeCost::Expensive,
    }
}

/// Имя маршрута из лога — в идентификатор, годный для журнала.
///
/// Имена в логе человеческие: флаг страны, точка-разделитель, пробелы. Всё
/// не-ASCII выбрасывается, разделители схлопываются в точку. Смысл не в
/// красоте: `RouteId` попадает в объяснения решений и в поиск по логу, а
/// искать по строке с эмодзи невозможно.
fn slug(name: &str) -> String {
    let mut out = String::new();
    let mut sep = false;
    for ch in name.chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            if sep && !out.is_empty() {
                out.push('.');
            }
            sep = false;
            out.push(ch);
        } else {
            sep = true;
        }
    }
    if out.is_empty() {
        "route".into()
    } else {
        out
    }
}

/// Двухбуквенный код страны из имени маршрута.
///
/// Эвристика по ИМЕНИ, а не по адресу: таблицы «адрес → страна» в ядре нет
/// и быть не должно. Если имя устроено иначе — страна пустая, и тогда
/// правила полосы по странам просто ни к чему не применяются, а в
/// объяснениях вместо страны стоит идентификатор. Это честнее, чем
/// угадывать.
fn country_from(slug: &str) -> String {
    for token in slug.split('.') {
        if let Some((_, tail)) = token.rsplit_once('-') {
            if tail.len() == 2 && tail.chars().all(|c| c.is_ascii_uppercase()) {
                return tail.to_string();
            }
        }
    }
    String::new()
}

// ──────────────────────────── Настройки прогона ────────────────────────────

/// Когда вызывать `reconcile`.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Tick {
    /// Раз на раунд измерений, в момент последней пробы раунда.
    ///
    /// Умолчание, и вот почему. Выдержка (`dwell`) в документе — «три
    /// ЗАМЕРА подряд», а не три тика таймера. Если тикать чаще, чем
    /// приходят данные, три тика подряд отработают на одних и тех же
    /// числах, и выдержка перестанет означать то, ради чего заведена.
    PerRound,
    /// Каждые N миллисекунд — как в живом клиенте, где `reconcile` зовёт
    /// таймер независимо от прихода измерений. Полезно, чтобы увидеть, во
    /// что превращаются те же константы при другом темпе решений.
    Every(u64),
}

#[derive(Clone, Debug)]
pub struct ReplayConfig {
    /// Сколько миллисекунд считать «не ответила». Порог — свойство НАШЕГО
    /// решения, а не сети; в логе его нет, поэтому он задаётся здесь и
    /// печатается в отчёте рядом с числами.
    pub timeout_ms: f32,
    pub tick: Tick,
    /// Брать ли в прогон петлевые записи (`self: true`). По умолчанию нет:
    /// это замер ноды самой в себя, а не путь пользователя.
    pub include_self: bool,
    /// Чем считать шаг проб для `C_age` и для порога устаревания. `None` —
    /// вывести из самого лога (медиана расстояния между соседними записями
    /// ОДНОГО маршрута).
    ///
    /// Брать шаг между пробами ВНУТРИ записи было бы неверно: знание о
    /// маршруте обновляется раз в раунд, а не двадцать раз за десять
    /// секунд, и возраст данных надо мерить именно раундами.
    pub probe_interval_ms: Option<u64>,
}

impl Default for ReplayConfig {
    fn default() -> Self {
        Self {
            // Столько же, сколько ждёт калибровочный сборщик: проба, не
            // уложившаяся в пять секунд, для любого класса трафика уже
            // бесполезна.
            timeout_ms: 5_000.0,
            tick: Tick::PerRound,
            include_self: false,
            probe_interval_ms: None,
        }
    }
}

// ──────────────────────────────── Отчёт ────────────────────────────────

/// Одно переключение полосы.
#[derive(Clone, Debug)]
pub struct SwitchRecord {
    pub at: Instant,
    pub lane: LaneId,
    /// Маршрут, с которого ушли. `None` — самый первый выбор за прогон:
    /// держаться было не за что. Это НЕ переключение, и в счётчик
    /// переключений оно не идёт.
    ///
    /// Здесь стоит последний НЕПУСТОЙ маршрут, а не то, что лежало в
    /// `LaneState::current`. Разница существенна: `OnEmpty::Block` обнуляет
    /// текущий маршрут, и следующий выбор формально выглядит «первым».
    /// Считать его первым значило бы прятать смену пути за блокировкой —
    /// человек-то видит именно смену.
    pub from: Option<RouteId>,
    pub to: RouteId,
    /// Между уходом и приходом полоса какое-то время стояла без маршрута.
    pub via_empty: bool,
    pub kind: ReasonKind,
    pub human_ru: String,
}

/// Что известно о маршруте по концу прогона.
#[derive(Clone, Debug)]
pub struct RouteStats {
    pub route: RouteId,
    pub node: NodeId,
    pub country: String,
    pub axis: Axis,
    pub samples: u32,
    pub lost: u32,
    pub rtt_p50: Option<f32>,
    pub rtt_p95: Option<f32>,
    pub pdv_ms: Option<f32>,
    pub mbps: Option<f32>,
    /// Нижняя граница Уилсона по доступности.
    pub availability_lo: f32,
}

/// Итог по одной полосе.
#[derive(Clone, Debug)]
pub struct LaneStats {
    pub lane: LaneId,
    pub title: String,
    pub ticks: usize,
    /// Первый выбор за прогон. Ровно один, если полоса вообще что-то
    /// выбрала.
    pub initial: usize,
    /// Настоящие переключения: был маршрут — стал другой.
    pub switches: Vec<SwitchRecord>,
    /// Сколько раз полоса оставалась без маршрута (эпизодов, а не тиков).
    pub empty_episodes: usize,
    pub drains: usize,
    /// Сколько миллисекунд полоса провела на каждом маршруте.
    pub time_on: BTreeMap<RouteId, u64>,
    /// Сколько — без маршрута вовсе.
    pub time_empty: u64,
    /// Причины по родам, в порядке первого появления.
    pub reasons: Vec<(ReasonKind, usize)>,
    /// Оценки на последнем тике: маршрут, значение, уверенность, ворота.
    pub final_scores: Vec<(RouteId, f32, f32, Vec<GateId>)>,
}

impl LaneStats {
    pub fn switches_per_hour(&self, span_ms: u64) -> f32 {
        if span_ms == 0 {
            return 0.0;
        }
        self.switches.len() as f32 * 3_600_000.0 / span_ms as f32
    }
}

#[derive(Clone, Debug)]
pub struct Report {
    pub records_total: usize,
    pub records_used: usize,
    pub records_self: usize,
    pub records_control: usize,
    pub records_core_failed: usize,
    pub records_unknown_transport: usize,
    pub bad_lines: usize,
    pub rounds: usize,
    /// От первой пробы до последней.
    pub span_ms: u64,
    /// Медианный шаг между раундами.
    pub round_step_ms: u64,
    pub probe_interval_ms: u64,
    pub ticks: usize,
    pub probes_total: usize,
    pub probes_lost: usize,
    /// Порог «не ответила», с которым шёл этот прогон. Печатается рядом с
    /// числами намеренно: он свойство НАШЕГО решения, а не сети, и без него
    /// доля потерь ничего не значит.
    pub timeout_ms: f32,
    /// Медиана задержки контрольной пробы прямым выходом, если она в логе
    /// есть. Точка отсчёта: всё, что медленнее её, — цена туннеля.
    pub control_rtt_p50: Option<f32>,
    pub routes: Vec<RouteStats>,
    pub lanes: Vec<LaneStats>,
}

// ──────────────────────────── Внутреннее ────────────────────────────

/// Событие для движка: либо проба, либо замер полосы.
enum Event {
    Probe(usize, Instant, ProbeOutcome),
    Throughput(usize, Instant, f32),
}

impl Event {
    fn at(&self) -> Instant {
        match self {
            Event::Probe(_, t, _) | Event::Throughput(_, t, _) => *t,
        }
    }
}

fn median(mut xs: Vec<u64>) -> u64 {
    if xs.is_empty() {
        return 0;
    }
    xs.sort_unstable();
    xs[xs.len() / 2]
}

fn bump(counts: &mut Vec<(ReasonKind, usize)>, kind: ReasonKind) {
    if let Some(e) = counts.iter_mut().find(|(k, _)| *k == kind) {
        e.1 += 1;
    } else {
        counts.push((kind, 1));
    }
}

// ──────────────────────────── Главный прогон ────────────────────────────

/// Прогнать записанный лог через движок.
///
/// Кандидатами каждой полосы становятся ВСЕ маршруты лога: что из них
/// полосе подходит, решают её же требования внутри `reconcile`, и решать
/// это дважды — в оснастке и в движке — значит проверять оснастку, а не
/// движок.
pub fn replay(log: &Log, lanes: &[Lane], cfg: &ReplayConfig) -> Report {
    // ── 1. Отбор записей и сборка каталога маршрутов.
    let mut routes: Vec<Route> = Vec::new();
    let mut countries: Vec<String> = Vec::new();
    let mut index: BTreeMap<String, usize> = BTreeMap::new();
    let mut used: Vec<&Record> = Vec::new();

    let mut records_self = 0;
    let mut records_control = 0;
    let mut records_core_failed = 0;
    let mut records_unknown_transport = 0;
    let mut control_rtts: Vec<f32> = Vec::new();

    for r in &log.records {
        if r.is_control() {
            records_control += 1;
            control_rtts.extend(r.probes.iter().filter(|p| p.ok).filter_map(|p| p.ms));
            continue;
        }
        if r.is_self && !cfg.include_self {
            records_self += 1;
            continue;
        }
        // Ядро сборщика не поднялось — измерить НЕ УДАЛОСЬ. Это наша
        // неспособность измерить, а не отказ маршрута, и засчитывать её
        // маршруту в потери нельзя: ровно эта путаница дважды стоила
        // проекту полдня отладки (см. `InvalidCause`). Запись просто не
        // даёт свидетельства ни в какую сторону.
        if !r.core_ok {
            records_core_failed += 1;
            continue;
        }
        let Some(axis) = axis_of_transport(&r.transport) else {
            records_unknown_transport += 1;
            continue;
        };
        let id = slug(&r.name);
        if !index.contains_key(&id) {
            let node = NodeId::new(if r.host.is_empty() { id.clone() } else { r.host.clone() });
            index.insert(id.clone(), routes.len());
            countries.push(country_from(&id));
            routes.push(Route {
                id: RouteId::new(id),
                node: node.clone(),
                transport: TransportId::new(r.transport.clone()),
                axis,
                exposure: Exposure::Tunnelled { node },
                // Что маршрут несёт, в логе не записано. Берётся умолчание
                // (TCP+UDP, только IPv4): все наши транспорты несут и то и
                // другое, а IPv6-egress на нодах намеренно выключен.
                carries: Carries::default(),
                handshake_cost: handshake_cost(&r.transport),
            });
        }
        used.push(r);
    }

    if used.is_empty() {
        return Report {
            records_total: log.records.len(),
            records_used: 0,
            records_self,
            records_control,
            records_core_failed,
            records_unknown_transport,
            bad_lines: log.bad_lines,
            rounds: 0,
            span_ms: 0,
            round_step_ms: 0,
            probe_interval_ms: cfg.probe_interval_ms.unwrap_or(0),
            ticks: 0,
            probes_total: 0,
            probes_lost: 0,
            timeout_ms: cfg.timeout_ms,
            control_rtt_p50: медиана_f32(&mut control_rtts),
            routes: Vec::new(),
            lanes: Vec::new(),
        };
    }

    // ── 2. Время. Ядру нужна монотонная разность, а не дата, поэтому нулём
    // объявляется первая использованная запись.
    let base = used.iter().map(|r| r.ts_s).fold(f64::INFINITY, f64::min);
    let ms_of = |ts: f64| -> u64 { ((ts - base) * 1000.0).max(0.0).round() as u64 };

    // ── 3. Развёртка записей в поток событий.
    //
    // Пробы внутри записи сборщик делает последовательно, одну за другой, и
    // собственных отметок времени у них нет. Поэтому проба помечается
    // моментом её ЗАВЕРШЕНИЯ: старт записи плюс сумма длительностей всех
    // предыдущих проб. Несостоявшейся пробе засчитывается полный таймаут —
    // столько она и заняла.
    let mut events: Vec<Event> = Vec::new();
    let mut probes_total = 0usize;
    let mut probes_lost = 0usize;
    let mut round_of_tick: BTreeMap<u64, u64> = BTreeMap::new();
    let mut per_route_row_ms: BTreeMap<usize, Vec<u64>> = BTreeMap::new();

    for r in &used {
        let idx = index[&slug(&r.name)];
        let mut t = ms_of(r.ts_s);
        per_route_row_ms.entry(idx).or_default().push(t);
        for p in &r.probes {
            probes_total += 1;
            let dur = p.ms.unwrap_or(cfg.timeout_ms).max(0.0);
            t = t.saturating_add(dur.round() as u64);
            let outcome = if p.ok {
                ProbeOutcome::Ok { rtt_ms: p.ms.unwrap_or(dur) }
            } else {
                probes_lost += 1;
                // Различить «не дошло» и «не пожали руки» лог не позволяет:
                // сборщик пишет только `ok: false`. Считаем таймаутом —
                // более мягкое из двух прочтений: провал рукопожатия
                // отдельно взводит аварийный факт, и приписывать его по
                // догадке значило бы переключать людей по нашей догадке.
                ProbeOutcome::Timeout
            };
            events.push(Event::Probe(idx, Instant(t), outcome));
        }
        if let Some(mbps) = r.mbps {
            events.push(Event::Throughput(idx, Instant(t), mbps));
        }
        let e = round_of_tick.entry(r.round).or_insert(0);
        *e = (*e).max(t);
    }

    events.sort_by_key(|e| e.at().0);
    let span_ms = events.last().map(|e| e.at().0).unwrap_or(0);

    // Шаг раунда — медиана расстояний между соседними записями ОДНОГО
    // маршрута. Именно с этим шагом обновляется знание о маршруте.
    let mut steps: Vec<u64> = Vec::new();
    for v in per_route_row_ms.values() {
        for w in v.windows(2) {
            if w[1] > w[0] {
                steps.push(w[1] - w[0]);
            }
        }
    }
    let round_step_ms = median(steps);
    let probe_interval_ms = cfg.probe_interval_ms.unwrap_or(round_step_ms.max(1));

    // ── 4. Моменты решений.
    let ticks: Vec<Instant> = match cfg.tick {
        Tick::PerRound => {
            let mut v: Vec<u64> = round_of_tick.values().copied().collect();
            v.sort_unstable();
            v.into_iter().map(Instant).collect()
        }
        Tick::Every(step) => {
            let step = step.max(1);
            let mut v = Vec::new();
            let mut t = step;
            while t <= span_ms {
                v.push(Instant(t));
                t += step;
            }
            // Последний тик обязан стоять на конце лога, иначе хвост
            // измерений не дойдёт до движка и итоговые числа в отчёте
            // будут посчитаны по неполным данным.
            if v.last().map(|x| x.0) != Some(span_ms) {
                v.push(Instant(span_ms));
            }
            v
        }
    };

    // ── 5. Сама симуляция.
    let mut healths: Vec<RouteHealth> = routes
        .iter()
        .map(|r| RouteHealth::new(r.id.clone(), r.axis))
        .collect();
    let mut last_throughput_at: Vec<Option<Instant>> = vec![None; routes.len()];

    let candidates: BTreeMap<LaneId, Vec<RouteId>> = lanes
        .iter()
        .map(|l| (l.id.clone(), routes.iter().map(|r| r.id.clone()).collect()))
        .collect();

    let mut state: BTreeMap<LaneId, LaneState> = BTreeMap::new();
    let mut damper = DamperState::new();

    let mut stats: Vec<LaneStats> = lanes
        .iter()
        .map(|l| LaneStats {
            lane: l.id.clone(),
            title: l.title.clone(),
            ticks: 0,
            initial: 0,
            switches: Vec::new(),
            empty_episodes: 0,
            drains: 0,
            time_on: BTreeMap::new(),
            time_empty: 0,
            reasons: Vec::new(),
            final_scores: Vec::new(),
        })
        .collect();

    let mut next_event = 0usize;
    let mut prev_tick = Instant(0);
    let mut last_snapshot: Option<Snapshot> = None;
    // Последний непустой маршрут полосы — отдельно от `LaneState::current`,
    // который обнуляется при блокировке.
    let mut last_route: Vec<Option<RouteId>> = vec![None; lanes.len()];
    let mut was_empty: Vec<bool> = vec![false; lanes.len()];

    for tick in &ticks {
        // Всё, что случилось до этого момента, движок к нему уже знает.
        while next_event < events.len() && events[next_event].at() <= *tick {
            match &events[next_event] {
                Event::Probe(idx, at, outcome) => {
                    healths[*idx].observe(&Probe {
                        route: routes[*idx].id.clone(),
                        at: *at,
                        outcome: outcome.clone(),
                    });
                }
                Event::Throughput(idx, at, mbps) => {
                    let dt = last_throughput_at[*idx].map_or(0, |p| at.since(p));
                    healths[*idx].observe_throughput(*mbps, dt);
                    last_throughput_at[*idx] = Some(*at);
                }
            }
            next_event += 1;
        }

        // Время, проведённое на маршруте, начисляется ЗА прошедший отрезок,
        // то есть тому маршруту, который на нём и стоял.
        let dt = tick.since(prev_tick);
        for (i, lane) in lanes.iter().enumerate() {
            match state.get(&lane.id).and_then(|s| s.current.clone()) {
                Some(cur) => *stats[i].time_on.entry(cur).or_insert(0) += dt,
                None => stats[i].time_empty += dt,
            }
        }
        prev_tick = *tick;

        let mut snap = Snapshot::new();
        for (i, r) in routes.iter().enumerate() {
            snap.insert(r.clone(), healths[i].clone(), countries[i].clone());
        }

        let (acts, reasons) =
            decide::reconcile(lanes, &candidates, &snap, &state, &mut damper, *tick, probe_interval_ms);

        for reason in &reasons {
            if let Some(i) = lanes.iter().position(|l| l.id == reason.lane) {
                bump(&mut stats[i].reasons, reason.kind);
            }
        }
        for act in acts {
            match act {
                Actuation::SelectLane { lane, route, reason } => {
                    let Some(i) = lanes.iter().position(|l| l.id == lane) else { continue };
                    let st = state.entry(lane.clone()).or_default();
                    if st.current.as_ref() == Some(&route) {
                        continue;
                    }
                    let from = last_route[i].clone();
                    let rec = SwitchRecord {
                        at: *tick,
                        lane,
                        from: from.clone(),
                        to: route.clone(),
                        via_empty: was_empty[i],
                        kind: reason.kind,
                        human_ru: reason.human_ru.clone(),
                    };
                    match &from {
                        // Возврат на тот же маршрут после блокировки —
                        // не переключение: путь не сменился.
                        Some(prev) if prev == &route => {}
                        Some(_) => stats[i].switches.push(rec),
                        None => stats[i].initial += 1,
                    }
                    st.current = Some(route.clone());
                    last_route[i] = Some(route);
                    was_empty[i] = false;
                }
                Actuation::Drain { lane } => {
                    if let Some(i) = lanes.iter().position(|l| l.id == lane) {
                        stats[i].drains += 1;
                    }
                }
                Actuation::GoEmpty { lane, action, .. } => {
                    // `HoldLast` тем и отличается, что маршрут остаётся, —
                    // просто про него честно сказано, что он не проверен.
                    if action == OnEmpty::HoldLast {
                        continue;
                    }
                    let Some(i) = lanes.iter().position(|l| l.id == lane) else { continue };
                    if !was_empty[i] {
                        stats[i].empty_episodes += 1;
                    }
                    state.entry(lane).or_default().current = None;
                    was_empty[i] = true;
                }
            }
        }

        for s in stats.iter_mut() {
            s.ticks += 1;
        }
        last_snapshot = Some(snap);
    }

    // ── 6. Итоги.
    if let Some(snap) = &last_snapshot {
        let now = ticks.last().copied().unwrap_or(Instant(0));
        for (i, lane) in lanes.iter().enumerate() {
            for r in &routes {
                let Some(h) = snap.health(&r.id) else { continue };
                let s = score::score(h, lane.sla, now, probe_interval_ms);
                stats[i].final_scores.push((
                    r.id.clone(),
                    s.value,
                    s.confidence,
                    s.gates_failed.clone(),
                ));
            }
            stats[i]
                .final_scores
                .sort_by(|a, b| b.1.total_cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
        }
    }

    let route_stats: Vec<RouteStats> = routes
        .iter()
        .enumerate()
        .map(|(i, r)| {
            let h = &healths[i];
            RouteStats {
                route: r.id.clone(),
                node: r.node.clone(),
                country: countries[i].clone(),
                axis: r.axis,
                samples: h.sample_count,
                lost: h.loss.total() - h.rtt_p50.count(),
                rtt_p50: h.rtt_p50.get(),
                rtt_p95: h.rtt_p95.get(),
                pdv_ms: h.pdv_ms(),
                mbps: h.throughput_mbps.as_ref().and_then(|e| e.get()),
                availability_lo: h.availability().lo,
            }
        })
        .collect();

    Report {
        records_total: log.records.len(),
        records_used: used.len(),
        records_self,
        records_control,
        records_core_failed,
        records_unknown_transport,
        bad_lines: log.bad_lines,
        rounds: round_of_tick.len(),
        span_ms,
        round_step_ms,
        probe_interval_ms,
        ticks: ticks.len(),
        probes_total,
        probes_lost,
        timeout_ms: cfg.timeout_ms,
        control_rtt_p50: медиана_f32(&mut control_rtts),
        routes: route_stats,
        lanes: stats,
    }
}

fn медиана_f32(xs: &mut Vec<f32>) -> Option<f32> {
    if xs.is_empty() {
        return None;
    }
    xs.sort_by(f32::total_cmp);
    Some(xs[xs.len() / 2])
}

// ──────────────────────────── Печать отчёта ────────────────────────────

fn мс(v: u64) -> String {
    let s = v / 1000;
    format!("{}:{:02}:{:02}", s / 3600, (s % 3600) / 60, s % 60)
}

fn кратко(gates: &[GateId]) -> String {
    if gates.is_empty() {
        return "—".into();
    }
    gates
        .iter()
        .map(|g| match g {
            GateId::Loss => "потери",
            GateId::RttTail => "хвост задержки",
            GateId::Availability => "доступность",
            GateId::DnsTampered => "подмена DNS",
            GateId::ExitUnverified => "выход не подтверждён",
            GateId::HandshakeFailed => "рукопожатие",
            GateId::Ipv6Leak => "утечка IPv6",
        })
        .collect::<Vec<_>>()
        .join(", ")
}

fn род_ru(k: ReasonKind) -> &'static str {
    match k {
        ReasonKind::Initial => "первый выбор",
        ReasonKind::Better => "сравнение оценок",
        ReasonKind::EmergencyFact => "аварийный факт",
        ReasonKind::AxisDead => "ось не проходит",
        ReasonKind::Suppressed => "отставлен за дребезг",
        ReasonKind::UserPinned => "закреплено человеком",
        ReasonKind::ModeChanged => "смена режима",
        ReasonKind::NoCandidate => "нет кандидата",
        ReasonKind::DamperOverridden => "подавление снято",
    }
}

impl Report {
    /// Отчёт для человека. Печатается примером `examples/replay.rs`.
    pub fn human_ru(&self) -> String {
        let mut o = String::new();
        o.push_str("═══ ПРОГОН НА ЗАПИСАННЫХ ИЗМЕРЕНИЯХ ═══\n\n");
        o.push_str(&format!(
            "Строк в логе: {} (в дело пошло {}, петлевых {}, контрольных {}, \
             ядро не поднялось {}, транспорт незнаком {}, битых {})\n",
            self.records_total,
            self.records_used,
            self.records_self,
            self.records_control,
            self.records_core_failed,
            self.records_unknown_transport,
            self.bad_lines
        ));
        o.push_str(&format!(
            "Раундов: {}, проб: {} (без ответа {} при пороге {:.0} с), период наблюдения: {}\n",
            self.rounds,
            self.probes_total,
            self.probes_lost,
            self.timeout_ms / 1000.0,
            мс(self.span_ms)
        ));
        o.push_str(&format!(
            "Шаг раунда: {:.0} с; шаг проб для C_age: {:.0} с; решений принято: {}\n",
            self.round_step_ms as f32 / 1000.0,
            self.probe_interval_ms as f32 / 1000.0,
            self.ticks
        ));
        if let Some(c) = self.control_rtt_p50 {
            o.push_str(&format!("Контроль (прямой выход), медиана: {c:.1} мс\n"));
        }

        o.push_str("\n─── Маршруты ───\n");
        o.push_str(&format!(
            "{:<26} {:<3} {:<26} {:>6} {:>5} {:>7} {:>7} {:>7} {:>7}\n",
            "маршрут", "стр", "ось", "проб", "нет", "p50", "p95", "p95-p50", "Мбит/с"
        ));
        let mut rs: Vec<&RouteStats> = self.routes.iter().collect();
        rs.sort_by(|a, b| a.route.cmp(&b.route));
        for r in rs {
            o.push_str(&format!(
                "{:<26} {:<3} {:<26} {:>6} {:>5} {:>7} {:>7} {:>7} {:>7}\n",
                r.route.as_str(),
                if r.country.is_empty() { "—" } else { &r.country },
                r.axis.human_ru(),
                r.samples,
                r.lost,
                r.rtt_p50.map_or("—".into(), |v| format!("{v:.0}")),
                r.rtt_p95.map_or("—".into(), |v| format!("{v:.0}")),
                r.pdv_ms.map_or("—".into(), |v| format!("{v:.0}")),
                r.mbps.map_or("—".into(), |v| format!("{v:.1}")),
            ));
        }

        for l in &self.lanes {
            o.push_str(&format!("\n─── Полоса «{}» ({}) ───\n", l.title, l.lane));
            o.push_str(&format!(
                "Решений: {}, первых выборов: {}, ПЕРЕКЛЮЧЕНИЙ: {} ({:.2} в час), \
                 эпизодов без маршрута: {}, обрывов: {}\n",
                l.ticks,
                l.initial,
                l.switches.len(),
                l.switches_per_hour(self.span_ms),
                l.empty_episodes,
                l.drains
            ));

            if l.switches.is_empty() {
                o.push_str("Переключений не было.\n");
            } else {
                for s in &l.switches {
                    o.push_str(&format!(
                        "  {}  {} → {}  [{}]{}\n            {}\n",
                        мс(s.at.0),
                        s.from.as_ref().map_or("—".into(), |r| r.to_string()),
                        s.to,
                        род_ru(s.kind),
                        if s.via_empty { "  (через блокировку)" } else { "" },
                        s.human_ru
                    ));
                }
            }

            let total: u64 = l.time_on.values().sum::<u64>() + l.time_empty;
            if total > 0 {
                o.push_str("Время на маршрутах:\n");
                let mut v: Vec<(&RouteId, &u64)> = l.time_on.iter().collect();
                v.sort_by(|a, b| b.1.cmp(a.1));
                for (r, t) in v {
                    o.push_str(&format!(
                        "  {:<26} {:>8}  ({:.1}%)\n",
                        r.as_str(),
                        мс(*t),
                        *t as f32 * 100.0 / total as f32
                    ));
                }
                if l.time_empty > 0 {
                    o.push_str(&format!(
                        "  {:<26} {:>8}  ({:.1}%)  ← без маршрута\n",
                        "—",
                        мс(l.time_empty),
                        l.time_empty as f32 * 100.0 / total as f32
                    ));
                }
            }

            if !l.reasons.is_empty() {
                o.push_str("Причины:\n");
                for (k, n) in &l.reasons {
                    o.push_str(&format!("  {:<24} {}\n", род_ru(*k), n));
                }
            }

            if !l.final_scores.is_empty() {
                o.push_str("Оценки на последнем решении:\n");
                for (r, v, c, g) in &l.final_scores {
                    o.push_str(&format!(
                        "  {:<26} {:>6.1}  уверенность {:.2}  ворота: {}\n",
                        r.as_str(),
                        v,
                        c,
                        кратко(g)
                    ));
                }
            }
        }
        o
    }
}

// ──────────────────────────────── Тесты ────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::axis::ExposureSet;
    use crate::lane::{Hysteresis, RouteRequirements, SwitchMode};
    use crate::score::SlaClass;

    /// Линейный конгруэнтный генератор. Не криптография и не претендует:
    /// нужен воспроизводимый шум без внешних зависимостей, чтобы тест
    /// падал и чинился одинаково у всех.
    struct Шум(u64);

    impl Шум {
        fn след(&mut self) -> f64 {
            // Множитель Кнута из MMIX.
            self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            (self.0 >> 11) as f64 / (1u64 << 53) as f64
        }
        /// Бокс–Мюллер: нормальное из двух равномерных.
        fn норм(&mut self, mu: f64, sd: f64) -> f64 {
            let u1 = self.след().max(1e-12);
            let u2 = self.след();
            mu + sd * (-2.0 * u1.ln()).sqrt() * (2.0 * std::f64::consts::PI * u2).cos()
        }
    }

    /// Собрать JSONL. Сборка идёт ТЕКСТОМ, а не структурами, нарочно: так
    /// тест проходит через тот же разбор, что и настоящий лог, и опечатка
    /// в разборе не спрячется за удобным конструктором.
    fn лог_текстом(
        имена: &[(&str, &str, &str)],
        раундов: usize,
        шаг_с: f64,
        мс_пробы: &mut dyn FnMut(usize, usize) -> Option<f64>,
    ) -> String {
        let mut s = String::new();
        for round in 0..раундов {
            for (i, (name, host, transport)) in имена.iter().enumerate() {
                let ts = 1_700_000_000.0 + round as f64 * шаг_с + i as f64 * 0.001;
                let p = match мс_пробы(round, i) {
                    Some(ms) => format!("{{\"ms\": {ms:.2}, \"connect_ms\": 1.0, \"ok\": true}}"),
                    None => "{\"ms\": null, \"connect_ms\": null, \"ok\": false}".to_string(),
                };
                s.push_str(&format!(
                    "{{\"ts\": {ts}, \"round\": {round}, \"name\": \"{name}\", \
                      \"host\": \"{host}\", \"transport\": \"{transport}\", \"self\": false, \
                      \"core_ok\": true, \"connect_ms\": 100.0, \"probes\": [{p}], \"mbps\": null}}\n"
                ));
            }
        }
        s
    }

    fn полоса() -> Lane {
        Lane {
            id: LaneId::new("web"),
            title: "Веб".into(),
            sla: SlaClass::Browse,
            allow: ExposureSet::TUNNELLED,
            justification: None,
            need: RouteRequirements::default(),
            // Единица, а не два: в тестах маршруты стоят на одной оси
            // намеренно, и жалоба на отсутствие запаса тут не по делу.
            min_axes: 1,
            on_empty: OnEmpty::Block,
            switch: SwitchMode::Drain,
            hysteresis: Hysteresis::default(),
        }
    }

    /// Шаг раунда 5 с и одна проба на раунд — ровно те условия, в которых
    /// в разделе 6.1 документа измерены α, margin, dwell и cooldown.
    /// Прогонять их при другом темпе значит проверять не те константы.
    const ШАГ_С: f64 = 5.0;

    #[test]
    fn два_одинаковых_маршрута_не_дают_переключений() {
        // Час наблюдения, N(45,6) на обоих — в точности постановка из
        // таблицы 6.1, где для α=1/8, margin=5, dwell=3, cooldown=60 с
        // обещан НОЛЬ ложных переключений в час.
        let mut ш = Шум(0x9E3779B97F4A7C15);
        let текст = лог_текстом(
            &[("node-A", "10.0.0.1", "trojan"), ("node-B", "10.0.0.2", "trojan")],
            720,
            ШАГ_С,
            &mut |_r, _i| Some(ш.норм(45.0, 6.0).max(1.0)),
        );
        let log = parse_jsonl(&текст);
        assert_eq!(log.bad_lines, 0, "разбор собственного же формата не удался");

        let lane = полоса();
        let rep = replay(&log, std::slice::from_ref(&lane), &ReplayConfig::default());

        let l = &rep.lanes[0];
        assert_eq!(l.initial, 1, "полоса выбиралась заново {} раз", l.initial);
        assert!(
            l.switches.is_empty(),
            "на двух объективно одинаковых маршрутах движок переключался {} раз: {:?}",
            l.switches.len(),
            l.switches.iter().map(|s| (s.at.0, s.human_ru.clone())).collect::<Vec<_>>()
        );
        // Предпосылка: маршруты действительно доходили до сравнения. Без
        // неё «ноль переключений» доказывался бы отсутствием кандидатов.
        assert!(
            l.time_on.values().sum::<u64>() > rep.span_ms / 2,
            "полоса больше половины времени простояла без маршрута — сравнивать было нечего"
        );
    }

    #[test]
    fn явная_деградация_даёт_ровно_одно_переключение_и_быстро() {
        // Первые полчаса маршруты равны, дальше A теряет каждую вторую
        // пробу. Это не «чуть хуже», а объективная негодность: ворота по
        // потерям у Browse — 5%.
        const ПЕРЕЛОМ: usize = 360; // 360 раундов по 5 с = 30 минут
        let mut ш = Шум(0x243F6A8885A308D3);
        let текст = лог_текстом(
            &[("node-A", "10.0.0.1", "trojan"), ("node-B", "10.0.0.2", "trojan")],
            720,
            ШАГ_С,
            &mut |r, i| {
                if i == 0 && r >= ПЕРЕЛОМ && r % 2 == 0 {
                    // Через одну — чтобы деградация была видна статистике,
                    // но НЕ взводила аварийный факт «три подряд». Иначе
                    // тест проверял бы аварийный путь, а не гистерезис.
                    return None;
                }
                Some(ш.норм(45.0, 6.0).max(1.0))
            },
        );
        let log = parse_jsonl(&текст);
        let lane = полоса();
        let rep = replay(&log, std::slice::from_ref(&lane), &ReplayConfig::default());

        let l = &rep.lanes[0];
        assert_eq!(
            l.switches.len(),
            1,
            "переключений {} вместо одного: {:?}",
            l.switches.len(),
            l.switches.iter().map(|s| (s.at.0, s.human_ru.clone())).collect::<Vec<_>>()
        );
        let s = &l.switches[0];
        assert_eq!(s.to, RouteId::new("node-B"), "ушли не туда");

        let перелом_мс = (ПЕРЕЛОМ as f64 * ШАГ_С * 1000.0) as u64;
        let задержка = s.at.0.saturating_sub(перелом_мс);
        // Потолок — три минуты. Он не подогнан под наблюдаемое (движок
        // укладывается примерно в две), а взят как граница смысла: за
        // временем жизни типичной веб-сессии реакция перестаёт быть
        // реакцией. Медианные 15 с из раздела 6.1 здесь неприменимы: там
        // мерили рост ЗАДЕРЖКИ, видимый на первом же замере, а потери
        // становятся видны только статистике — верхняя граница Уилсона
        // перевалит порог ворот не раньше, чем накопится два десятка проб.
        assert!(
            задержка <= 180_000,
            "движок опомнился только через {} с после деградации",
            задержка / 1000
        );
    }

    #[test]
    fn петлевые_и_контрольные_записи_в_сравнение_не_идут() {
        let текст = "\
{\"ts\": 1.0, \"round\": 0, \"name\": \"control\", \"host\": \"0.0.0.0\", \"transport\": \"direct\", \"self\": false, \"core_ok\": true, \"connect_ms\": 1.0, \"probes\": [{\"ms\": 10.0, \"connect_ms\": 0.2, \"ok\": true}], \"mbps\": 260.0}
{\"ts\": 2.0, \"round\": 0, \"name\": \"loop-LT\", \"host\": \"1.1.1.1\", \"transport\": \"trojan\", \"self\": true, \"core_ok\": true, \"connect_ms\": 1.0, \"probes\": [{\"ms\": 11.0, \"connect_ms\": 0.2, \"ok\": true}], \"mbps\": null}
{\"ts\": 3.0, \"round\": 0, \"name\": \"dead-US\", \"host\": \"2.2.2.2\", \"transport\": \"trojan\", \"self\": false, \"core_ok\": false, \"connect_ms\": null, \"probes\": [], \"mbps\": null}
{\"ts\": 4.0, \"round\": 0, \"name\": \"good-FI\", \"host\": \"3.3.3.3\", \"transport\": \"trojan\", \"self\": false, \"core_ok\": true, \"connect_ms\": 1.0, \"probes\": [{\"ms\": 40.0, \"connect_ms\": 0.2, \"ok\": true}], \"mbps\": null}
не json вовсе
";
        let log = parse_jsonl(текст);
        assert_eq!(log.bad_lines, 1);
        let lane = полоса();
        let rep = replay(&log, std::slice::from_ref(&lane), &ReplayConfig::default());
        assert_eq!(rep.records_control, 1);
        assert_eq!(rep.records_self, 1);
        assert_eq!(rep.records_core_failed, 1);
        assert_eq!(rep.routes.len(), 1, "в каталог попало лишнее: {:?}", rep.routes);
        assert_eq!(rep.routes[0].route, RouteId::new("good-FI"));
        assert_eq!(rep.routes[0].country, "FI", "страна не вынута из имени");
        assert_eq!(rep.control_rtt_p50, Some(10.0));
        // Строка с неподнявшимся ядром не должна выглядеть отказом
        // маршрута: маршрута из неё не возникает вовсе.
        assert!(rep.routes.iter().all(|r| r.route != RouteId::new("dead-US")));
    }

    #[test]
    fn ось_выводится_из_транспорта_а_незнакомый_отбрасывается() {
        assert_eq!(axis_of_transport("hysteria2"), Some(Axis::QuicUdp));
        assert_eq!(axis_of_transport("vless-grpc"), Some(Axis::FakeTlsH2));
        assert_eq!(axis_of_transport("vless-vision"), Some(Axis::FakeTlsTcp));
        assert_eq!(axis_of_transport("trojan"), Some(Axis::RealTls));
        assert_eq!(axis_of_transport("ss"), Some(Axis::RawStream));
        // Приписать незнакомому транспорту ось наугад значит соврать
        // осевому вердикту, а он решает, обвинять сеть или сервер.
        assert_eq!(axis_of_transport("wireguard"), None);
    }

    #[test]
    fn имя_с_эмодзи_превращается_в_искомый_идентификатор() {
        assert_eq!(slug("🇺🇸 NOT_A_RKN-US · alt"), "NOT_A_RKN-US.alt");
        assert_eq!(country_from("NOT_A_RKN-US.alt"), "US");
        assert_eq!(country_from("node.without.code"), "");
    }
}
