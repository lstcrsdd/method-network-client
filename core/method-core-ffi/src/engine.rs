//! Движок: всё, что за границей C, но ещё на Rust.
//!
//! Здесь нет ни одного `unsafe` и ни одного сырого указателя — только владение
//! состоянием, которое ядро намеренно не держит: каталог маршрутов и полос,
//! накопленное здоровье, состояние полос и демпфер. Ядро остаётся чистой
//! функцией; хранилище живёт этажом ниже границы, потому что гонять его через
//! границу на каждый вызов означало бы сериализовать десяток структур в
//! секунду ради удовольствия.
//!
//! Разделение полезно и для проверки: этот файл тестируется обычными тестами
//! Rust, а `ffi.rs` отвечает ровно за одно — за то, чтобы указатели и паники
//! не пересекли границу.

use std::collections::BTreeMap;

use method_core::axis::{Axis, Exposure, ExposureSet};
use method_core::damper::DamperState;
use method_core::decide::{self, Actuation, LaneState, ReasonKind, Snapshot};
use method_core::ids::{LaneId, NodeId, RouteId, TransportId};
use method_core::lane::{Hysteresis, Lane, OnEmpty, RouteRequirements, SwitchMode};
use method_core::metrics::{Probe, ProbeOutcome, RouteHealth};
use method_core::route::{Carries, HandshakeCost, Route};
use method_core::score::{self, GateId, MetricId, SlaClass};
use method_core::Instant;

use crate::abi;
use crate::error::{Fail, Status};

// ─────────────────────────── Входные описания ───────────────────────────
//
// Отдельные типы, а не `McRouteDesc` напрямую: сюда приходит уже проверенное
// и разобранное. Граница отвечает за указатели, движок — за смысл.

pub struct RouteInput {
    pub id: String,
    pub node: String,
    pub transport: String,
    pub country: String,
    pub axis: Axis,
    pub exposure: Exposure,
    pub carries: Carries,
    pub handshake_cost: HandshakeCost,
}

pub struct LaneInput {
    pub id: String,
    pub title: String,
    pub sla: SlaClass,
    pub allow: ExposureSet,
    pub justification: Option<String>,
    pub need: RouteRequirements,
    pub min_axes: u8,
    pub on_empty: OnEmpty,
    pub switch: SwitchMode,
    pub hysteresis: Hysteresis,
}

// ─────────────────────────── Выходные решения ───────────────────────────

pub struct Action {
    pub kind: i32,
    pub lane: u32,
    pub route: u32,
    pub on_empty: i32,
    pub on_empty_lane: u32,
    pub reason_kind: i32,
    pub text: String,
}

pub struct Reason {
    pub kind: i32,
    pub lane: u32,
    pub text: String,
}

/// Решение целиком. Строки принадлежат ЕМУ и живут ровно столько же.
pub struct Decision {
    pub actions: Vec<Action>,
    pub reasons: Vec<Reason>,
}

// ──────────────────────────────── Движок ────────────────────────────────

struct RouteSlot {
    route: Route,
    country: String,
}

pub struct Engine {
    /// Шаг проб. Нужен оценке (множитель возраста в уверенности) и осевому
    /// вердикту (когда замер считать протухшим). Задаётся один раз при
    /// создании: величина описывает НАШ измерительный контур, а не отдельный
    /// вызов, и меняться посреди работы ей незачем.
    probe_interval_ms: u64,

    /// Каталог. Дескриптор — это индекс+1, поэтому маршруты и полосы никогда
    /// не удаляются: удаление сдвинуло бы чужие дескрипторы или оставило бы
    /// дыры, а дескриптор обязан быть верным всё время жизни движка. Каталог
    /// поменялся — создаётся новый движок и в него загружается состояние
    /// (история хранится по строковым идентификаторам и переживает это без
    /// потерь).
    routes: Vec<RouteSlot>,
    lanes: Vec<Lane>,
    route_ix: BTreeMap<RouteId, u32>,
    lane_ix: BTreeMap<LaneId, u32>,

    /// Здоровье живёт по СТРОКОВОМУ идентификатору, а не по дескриптору:
    /// дескриптор — свойство текущего запуска, а история обязана пережить
    /// перезапуск.
    health: BTreeMap<RouteId, RouteHealth>,
    /// Когда в последний раз мерили полосу пропускания. В `RouteHealth` такого
    /// поля нет, а EWMA полосы сглаживается по времени и требует шага.
    throughput_at: BTreeMap<RouteId, Instant>,

    /// Состояние полос ведёт исполнитель (см. `lane_applied`), движок лишь
    /// хранит подтверждённое.
    lane_state: BTreeMap<LaneId, LaneState>,
    damper: DamperState,
}

impl Engine {
    pub fn new(probe_interval_ms: u64) -> Result<Engine, Fail> {
        if probe_interval_ms == 0 {
            return Err(Fail::invalid(
                "шаг проб не может быть нулевым: на него делят при расчёте возраста замера",
            ));
        }
        Ok(Engine {
            probe_interval_ms,
            routes: Vec::new(),
            lanes: Vec::new(),
            route_ix: BTreeMap::new(),
            lane_ix: BTreeMap::new(),
            health: BTreeMap::new(),
            throughput_at: BTreeMap::new(),
            lane_state: BTreeMap::new(),
            damper: DamperState::new(),
        })
    }

    pub fn probe_interval_ms(&self) -> u64 {
        self.probe_interval_ms
    }

    // ── Каталог ──

    pub fn add_route(&mut self, input: RouteInput) -> Result<u32, Fail> {
        let id = RouteId::new(input.id);
        if self.route_ix.contains_key(&id) {
            return Err(Fail::new(
                Status::Duplicate,
                format!("маршрут «{id}» уже объявлен"),
            ));
        }
        if !input.carries.tcp && !input.carries.udp {
            return Err(Fail::invalid(format!(
                "маршрут «{id}» не несёт ни TCP, ни UDP — по нему нельзя пустить ничего"
            )));
        }
        // Ось и экспозиция обязаны быть согласованы. Туннельный маршрут всегда
        // идёт каким-то СПОСОБОМ пройти, а прямой выход и блокировка оси не
        // имеют вовсе. Рассогласование тихо ломает осевой вердикт: он считает
        // узлы, на которых «ось мертва», и лишний маршрут без транспорта на
        // оси `none` испортил бы счёт.
        match (&input.exposure, input.axis) {
            (Exposure::Tunnelled { .. }, Axis::None) => {
                return Err(Fail::invalid(format!(
                    "маршрут «{id}» идёт через узел, но стоит на оси «без туннеля»"
                )))
            }
            (Exposure::Direct | Exposure::Blocked, a) if a != Axis::None => {
                return Err(Fail::invalid(format!(
                    "маршрут «{id}» не идёт через туннель, значит оси у него нет: ожидалась «без туннеля»"
                )))
            }
            _ => {}
        }
        let route = Route {
            id: id.clone(),
            node: NodeId::new(input.node),
            transport: TransportId::new(input.transport),
            axis: input.axis,
            exposure: input.exposure,
            carries: input.carries,
            handshake_cost: input.handshake_cost,
        };

        // История могла приехать из сохранённого состояния РАНЬШЕ каталога.
        // Если она про другую ось — это уже другой маршрут под тем же именем,
        // и сравнивать его прошлое с настоящим нельзя.
        match self.health.get(&id) {
            Some(h) if h.axis != route.axis => {
                self.health.insert(id.clone(), RouteHealth::new(id.clone(), route.axis));
                self.throughput_at.remove(&id);
            }
            Some(_) => {}
            None => {
                self.health.insert(id.clone(), RouteHealth::new(id.clone(), route.axis));
            }
        }

        self.routes.push(RouteSlot { route, country: input.country });
        let handle = self.routes.len() as u32;
        self.route_ix.insert(id, handle);
        Ok(handle)
    }

    pub fn add_lane(&mut self, input: LaneInput) -> Result<u32, Fail> {
        let id = LaneId::new(input.id);
        if self.lane_ix.contains_key(&id) {
            return Err(Fail::new(
                Status::Duplicate,
                format!("полоса «{id}» уже объявлена"),
            ));
        }
        if input.allow.is_empty() {
            return Err(Fail::invalid(format!(
                "полосе «{id}» не разрешена ни одна экспозиция — ей нечем пользоваться"
            )));
        }
        // Маска экспозиции — не произвольное число. Лишний бит означал бы
        // разрешение, которого не существует, и полоса молча перестала бы
        // принимать хоть что-нибудь: всякий маршрут проверяется на вхождение
        // в маску, а неизвестному биту не соответствует ни один.
        const KNOWN: u8 = 1 | 2 | 4;
        if input.allow.0 & !KNOWN != 0 {
            return Err(Fail::invalid(format!(
                "в маске экспозиции полосы «{id}» есть неизвестные биты: 0x{:x}",
                input.allow.0
            )));
        }
        // Продуктовое правило ядра, вынесенное на границу: полоса, которой
        // разрешён прямой выход, ОБЯЗАНА объяснить зачем. «56 доменов» человек
        // проматывает, а объяснение заставляет остановиться. Проверять это
        // здесь дешевле, чем ловить потом в разнице планов.
        if input.allow.permits_direct()
            && input.justification.as_deref().unwrap_or("").trim().is_empty()
        {
            return Err(Fail::invalid(format!(
                "полосе «{id}» разрешён прямой выход, но не сказано зачем: \
                 поле justification обязательно"
            )));
        }
        if input.hysteresis.margin_floor < 0.0 || !input.hysteresis.margin_floor.is_finite() {
            return Err(Fail::invalid("порог переключения обязан быть конечным и неотрицательным"));
        }
        if input.hysteresis.cooldown_max_ms < input.hysteresis.cooldown_ms {
            return Err(Fail::invalid(
                "потолок остывания меньше базового остывания — остывание никогда не сработает",
            ));
        }
        if let OnEmpty::Fallback { lane: ref target } = input.on_empty {
            if *target == id {
                return Err(Fail::invalid(format!(
                    "полоса «{id}» отсылает при пустоте сама к себе"
                )));
            }
            // Цель обязана быть объявлена РАНЬШЕ. Это не каприз: так цикл
            // становится невозможен по построению (новая полоса умеет
            // ссылаться только назад), и проверять ацикличность отдельно не
            // нужно. Длину цепочки (не длиннее двух) стережёт компилятор
            // планов этажом выше — здесь мы про неё ничего не знаем.
            if !self.lane_ix.contains_key(target) {
                return Err(Fail::not_found(format!(
                    "запасная полоса «{target}» не объявлена; объявляй её раньше «{id}»"
                )));
            }
        }

        self.lanes.push(Lane {
            id: id.clone(),
            title: input.title,
            sla: input.sla,
            allow: input.allow,
            justification: input.justification,
            need: input.need,
            min_axes: input.min_axes,
            on_empty: input.on_empty,
            switch: input.switch,
            hysteresis: input.hysteresis,
        });
        let handle = self.lanes.len() as u32;
        self.lane_ix.insert(id, handle);
        Ok(handle)
    }

    pub fn route_count(&self) -> usize {
        self.routes.len()
    }

    pub fn lane_count(&self) -> usize {
        self.lanes.len()
    }

    pub fn route_handle(&self, id: &str) -> Result<u32, Fail> {
        self.route_ix
            .get(&RouteId::new(id))
            .copied()
            .ok_or_else(|| Fail::not_found(format!("маршрут «{id}» не объявлен")))
    }

    pub fn lane_handle(&self, id: &str) -> Result<u32, Fail> {
        self.lane_ix
            .get(&LaneId::new(id))
            .copied()
            .ok_or_else(|| Fail::not_found(format!("полоса «{id}» не объявлена")))
    }

    pub fn route_id(&self, handle: u32) -> Result<&str, Fail> {
        Ok(self.route_slot(handle)?.route.id.as_str())
    }

    pub fn lane_id(&self, handle: u32) -> Result<&str, Fail> {
        Ok(self.lane(handle)?.id.as_str())
    }

    fn route_slot(&self, handle: u32) -> Result<&RouteSlot, Fail> {
        if handle == 0 {
            return Err(Fail::handle("нулевой дескриптор маршрута"));
        }
        self.routes
            .get(handle as usize - 1)
            .ok_or_else(|| Fail::handle(format!("дескриптор маршрута {handle} неизвестен")))
    }

    fn lane(&self, handle: u32) -> Result<&Lane, Fail> {
        if handle == 0 {
            return Err(Fail::handle("нулевой дескриптор полосы"));
        }
        self.lanes
            .get(handle as usize - 1)
            .ok_or_else(|| Fail::handle(format!("дескриптор полосы {handle} неизвестен")))
    }

    // ── Измерения ──

    pub fn observe(
        &mut self,
        route: u32,
        at: Instant,
        outcome: ProbeOutcome,
    ) -> Result<(), Fail> {
        let slot = self.route_slot(route)?;
        let id = slot.route.id.clone();
        // Ожидаемым узлом выхода всегда является узел САМОГО маршрута: если бы
        // трафик должен был выйти где-то ещё, это был бы другой маршрут.
        // Граница поэтому и не спрашивает «ожидаемый узел» — спросить значило
        // бы позволить вызывающему рассогласовать его с каталогом.
        let outcome = match outcome {
            ProbeOutcome::ExitMismatch { got, .. } => {
                ProbeOutcome::ExitMismatch { expected: slot.route.node.clone(), got }
            }
            other => other,
        };
        let probe = Probe { route: id.clone(), at, outcome };
        let h = self
            .health
            .get_mut(&id)
            .ok_or_else(|| Fail::new(Status::Internal, format!("здоровье маршрута «{id}» потеряно")))?;
        h.observe(&probe);
        Ok(())
    }

    pub fn observe_throughput(&mut self, route: u32, at: Instant, mbps: f32) -> Result<(), Fail> {
        if !mbps.is_finite() || mbps < 0.0 {
            return Err(Fail::invalid("полоса пропускания обязана быть конечной и неотрицательной"));
        }
        let id = self.route_slot(route)?.route.id.clone();
        // Шаг считаем от прошлого замера ПОЛОСЫ, а не от прошлой пробы:
        // EWMA сглаживается по времени, и подмешивать сюда частоту проб —
        // значит сглаживать полосу с чужой инерцией.
        let dt = self.throughput_at.get(&id).map_or(0, |p| at.since(*p));
        let h = self
            .health
            .get_mut(&id)
            .ok_or_else(|| Fail::new(Status::Internal, format!("здоровье маршрута «{id}» потеряно")))?;
        h.observe_throughput(mbps, dt);
        self.throughput_at.insert(id, at);
        Ok(())
    }

    /// Сеть сменилась: всё накопленное обесценивается.
    ///
    /// Задержки через другой канал с прежними несравнимы, а разность между
    /// ними — не джиттер. Штрафы за флап тоже: маршрут дребезжал в ТОЙ сети.
    /// Забываем и то и другое, каталог остаётся, дескрипторы остаются.
    pub fn network_changed(&mut self) {
        let ids: Vec<RouteId> = self.health.keys().cloned().collect();
        for id in &ids {
            if let Some(h) = self.health.get_mut(id) {
                h.reset_for_new_network();
            }
            decide::forget_route_everywhere(&mut self.damper, &self.lanes, id);
        }
        self.throughput_at.clear();
    }

    // ── Состояние полос ──

    /// Исполнитель подтвердил, что маршрут ДЕЙСТВИТЕЛЬНО поставлен.
    ///
    /// Раздельно с `reconcile` намеренно. Только исполнитель знает, применилось
    /// ли решение: между «движок решил» и «ядро переключило селектор» лежит
    /// HTTP-вызов, который может не дойти. Если бы `reconcile` обновлял
    /// состояние сам, движок считал бы полосу переключённой при любом исходе —
    /// и молчал бы ровно тогда, когда переключение не состоялось.
    pub fn lane_applied(&mut self, lane: u32, route: u32) -> Result<(), Fail> {
        let lane_id = self.lane(lane)?.id.clone();
        let route_id = self.route_slot(route)?.route.id.clone();
        self.lane_state.entry(lane_id).or_default().current = Some(route_id);
        Ok(())
    }

    pub fn lane_cleared(&mut self, lane: u32) -> Result<(), Fail> {
        let lane_id = self.lane(lane)?.id.clone();
        self.lane_state.entry(lane_id).or_default().current = None;
        Ok(())
    }

    /// Дескриптор текущего маршрута полосы; 0 — маршрута нет.
    pub fn lane_current(&self, lane: u32) -> Result<u32, Fail> {
        let lane_id = self.lane(lane)?.id.clone();
        let Some(cur) = self.lane_state.get(&lane_id).and_then(|s| s.current.clone()) else {
            return Ok(0);
        };
        Ok(self.route_ix.get(&cur).copied().unwrap_or(0))
    }

    pub fn lane_pin(&mut self, lane: u32, route: u32, until: Instant) -> Result<(), Fail> {
        let lane_id = self.lane(lane)?.id.clone();
        let route_id = self.route_slot(route)?.route.id.clone();
        self.lane_state.entry(lane_id).or_default().pin = Some((route_id, until));
        Ok(())
    }

    pub fn lane_unpin(&mut self, lane: u32) -> Result<(), Fail> {
        let lane_id = self.lane(lane)?.id.clone();
        self.lane_state.entry(lane_id).or_default().pin = None;
        Ok(())
    }

    // ── Решение ──

    fn snapshot(&self) -> Snapshot {
        let mut snap = Snapshot::new();
        for slot in &self.routes {
            let Some(h) = self.health.get(&slot.route.id) else { continue };
            snap.insert(slot.route.clone(), h.clone(), slot.country.clone());
        }
        snap
    }

    pub fn reconcile(&mut self, now: Instant) -> Decision {
        // Кандидатами каждой полосы становятся ВСЕ маршруты каталога. Что из
        // них полосе подходит, решают её же требования внутри `reconcile`, и
        // решать это дважды — здесь и в ядре — значит проверять оснастку, а
        // не движок. Ровно так же устроен прогон на записанном логе.
        let all: Vec<RouteId> = self.routes.iter().map(|s| s.route.id.clone()).collect();
        let candidates: BTreeMap<LaneId, Vec<RouteId>> =
            self.lanes.iter().map(|l| (l.id.clone(), all.clone())).collect();

        let snap = self.snapshot();
        let (acts, reasons) = decide::reconcile(
            &self.lanes,
            &candidates,
            &snap,
            &self.lane_state,
            &mut self.damper,
            now,
            self.probe_interval_ms,
        );

        let actions = acts
            .into_iter()
            .map(|a| match a {
                Actuation::SelectLane { lane, route, reason } => Action {
                    kind: abi::MC_ACTION_SELECT,
                    lane: self.lane_ix.get(&lane).copied().unwrap_or(0),
                    route: self.route_ix.get(&route).copied().unwrap_or(0),
                    on_empty: -1,
                    on_empty_lane: 0,
                    reason_kind: reason_code(reason.kind),
                    text: reason.human_ru,
                },
                Actuation::Drain { lane } => Action {
                    kind: abi::MC_ACTION_DRAIN,
                    lane: self.lane_ix.get(&lane).copied().unwrap_or(0),
                    route: 0,
                    on_empty: -1,
                    on_empty_lane: 0,
                    reason_kind: abi::MC_REASON_NONE,
                    text: String::new(),
                },
                Actuation::GoEmpty { lane, action, reason } => {
                    let (code, target) = match &action {
                        OnEmpty::Block => (abi::MC_ON_EMPTY_BLOCK, 0),
                        OnEmpty::HoldLast => (abi::MC_ON_EMPTY_HOLD_LAST, 0),
                        OnEmpty::Fallback { lane: to } => (
                            abi::MC_ON_EMPTY_FALLBACK,
                            self.lane_ix.get(to).copied().unwrap_or(0),
                        ),
                    };
                    Action {
                        kind: abi::MC_ACTION_GO_EMPTY,
                        lane: self.lane_ix.get(&lane).copied().unwrap_or(0),
                        route: 0,
                        on_empty: code,
                        on_empty_lane: target,
                        reason_kind: reason_code(reason.kind),
                        text: reason.human_ru,
                    }
                }
            })
            .collect();

        let reasons = reasons
            .into_iter()
            .map(|r| Reason {
                kind: reason_code(r.kind),
                // Вердикт про сеть целиком приходит с полосой-заглушкой «*»,
                // которой в каталоге нет: отдаём ноль и говорим об этом в
                // заголовке — иначе пришлось бы заводить фальшивую полосу.
                lane: self.lane_ix.get(&r.lane).copied().unwrap_or(0),
                text: r.human_ru,
            })
            .collect();

        Decision { actions, reasons }
    }

    // ── Оценка ──

    /// Оценка маршрута под класс нагрузки.
    ///
    /// Считается РОВНО тем же вызовом, каким её считает контур решений, без
    /// истории стабильности. Это сознательно: показать человеку число,
    /// отличное от того, по которому принято решение, — верный способ
    /// получить вопрос «почему выбран не тот, у кого больше».
    pub fn score(&self, route: u32, sla: SlaClass, now: Instant) -> Result<abi::McScore, Fail> {
        let id = self.route_slot(route)?.route.id.clone();
        let h = self
            .health
            .get(&id)
            .ok_or_else(|| Fail::new(Status::Internal, format!("здоровье маршрута «{id}» потеряно")))?;
        let s = score::score(h, sla, now, self.probe_interval_ms);

        let mut gates = 0u32;
        for g in &s.gates_failed {
            gates |= gate_bit(*g);
        }
        let (has_band, lo, hi) = match s.band {
            Some((lo, hi)) => (1u8, lo, hi),
            None => (0u8, 0.0, 0.0),
        };
        let display = match s.display() {
            score::Display::Measuring => abi::MC_DISPLAY_MEASURING,
            score::Display::Band { .. } => abi::MC_DISPLAY_BAND,
            score::Display::Value { .. } => abi::MC_DISPLAY_VALUE,
        };
        Ok(abi::McScore {
            value: s.value,
            band_lo: lo,
            band_hi: hi,
            confidence: s.confidence,
            limiter: metric_code(s.limiter),
            display,
            gates,
            has_band,
        })
    }

    // ── Сохранение и загрузка ──

    pub fn health_entries(
        &self,
    ) -> impl Iterator<Item = (&RouteId, &RouteHealth, Option<Instant>)> + '_ {
        self.health
            .iter()
            .map(move |(id, h)| (id, h, self.throughput_at.get(id).copied()))
    }

    /// Положить восстановленное здоровье. Возвращает `false`, если запись
    /// отброшена: маршрут с таким именем уже объявлен, но стоит на другой оси,
    /// — значит, это другой маршрут под старым именем, и его прошлое к делу
    /// не относится.
    pub fn restore_health(
        &mut self,
        id: RouteId,
        health: RouteHealth,
        throughput_at: Option<Instant>,
    ) -> bool {
        if let Ok(handle) = self.route_handle(id.as_str()) {
            if let Ok(slot) = self.route_slot(handle) {
                if slot.route.axis != health.axis {
                    return false;
                }
            }
        }
        self.health.insert(id.clone(), health);
        match throughput_at {
            Some(t) => {
                self.throughput_at.insert(id, t);
            }
            None => {
                self.throughput_at.remove(&id);
            }
        }
        true
    }
}

// ─────────────────────── Перевод перечней в числа ───────────────────────

fn reason_code(k: ReasonKind) -> i32 {
    match k {
        ReasonKind::Initial => abi::MC_REASON_INITIAL,
        ReasonKind::Better => abi::MC_REASON_BETTER,
        ReasonKind::EmergencyFact => abi::MC_REASON_EMERGENCY_FACT,
        ReasonKind::AxisDead => abi::MC_REASON_AXIS_DEAD,
        ReasonKind::Suppressed => abi::MC_REASON_SUPPRESSED,
        ReasonKind::UserPinned => abi::MC_REASON_USER_PINNED,
        ReasonKind::ModeChanged => abi::MC_REASON_MODE_CHANGED,
        ReasonKind::NoCandidate => abi::MC_REASON_NO_CANDIDATE,
        ReasonKind::DamperOverridden => abi::MC_REASON_DAMPER_OVERRIDDEN,
    }
}

fn metric_code(m: MetricId) -> i32 {
    match m {
        MetricId::Rtt => abi::MC_METRIC_RTT,
        MetricId::Jitter => abi::MC_METRIC_JITTER,
        MetricId::Loss => abi::MC_METRIC_LOSS,
        MetricId::Throughput => abi::MC_METRIC_THROUGHPUT,
        MetricId::Stability => abi::MC_METRIC_STABILITY,
    }
}

fn gate_bit(g: GateId) -> u32 {
    match g {
        GateId::Loss => abi::MC_GATE_LOSS,
        GateId::RttTail => abi::MC_GATE_RTT_TAIL,
        GateId::Availability => abi::MC_GATE_AVAILABILITY,
        GateId::DnsTampered => abi::MC_GATE_DNS_TAMPERED,
        GateId::ExitUnverified => abi::MC_GATE_EXIT_UNVERIFIED,
        GateId::HandshakeFailed => abi::MC_GATE_HANDSHAKE_FAILED,
        GateId::Ipv6Leak => abi::MC_GATE_IPV6_LEAK,
    }
}

pub fn axis_from(code: i32) -> Result<Axis, Fail> {
    Ok(match code {
        abi::MC_AXIS_QUIC_UDP => Axis::QuicUdp,
        abi::MC_AXIS_FAKE_TLS_H2 => Axis::FakeTlsH2,
        abi::MC_AXIS_FAKE_TLS_TCP => Axis::FakeTlsTcp,
        abi::MC_AXIS_REAL_TLS => Axis::RealTls,
        abi::MC_AXIS_RAW_STREAM => Axis::RawStream,
        abi::MC_AXIS_NONE => Axis::None,
        other => return Err(Fail::invalid(format!("нет оси обхода с кодом {other}"))),
    })
}

pub fn sla_from(code: i32) -> Result<SlaClass, Fail> {
    Ok(match code {
        abi::MC_SLA_REALTIME => SlaClass::Realtime,
        abi::MC_SLA_BROWSE => SlaClass::Browse,
        abi::MC_SLA_STREAM => SlaClass::Stream,
        abi::MC_SLA_BULK => SlaClass::Bulk,
        abi::MC_SLA_SENSITIVE => SlaClass::Sensitive,
        other => return Err(Fail::invalid(format!("нет класса нагрузки с кодом {other}"))),
    })
}

pub fn handshake_from(code: i32) -> Result<HandshakeCost, Fail> {
    Ok(match code {
        abi::MC_HANDSHAKE_CHEAP => HandshakeCost::Cheap,
        abi::MC_HANDSHAKE_EXPENSIVE => HandshakeCost::Expensive,
        other => return Err(Fail::invalid(format!("нет цены рукопожатия с кодом {other}"))),
    })
}

pub fn switch_from(code: i32) -> Result<SwitchMode, Fail> {
    Ok(match code {
        abi::MC_SWITCH_DRAIN => SwitchMode::Drain,
        abi::MC_SWITCH_CUT => SwitchMode::Cut,
        other => return Err(Fail::invalid(format!("нет режима смены с кодом {other}"))),
    })
}

pub fn discard_cause_from(code: i32) -> Result<method_core::metrics::InvalidCause, Fail> {
    use method_core::metrics::InvalidCause as C;
    Ok(match code {
        abi::MC_DISCARD_DEFAULT_ROUTE_THROUGH_TUNNEL => C::DefaultRouteThroughTunnel,
        abi::MC_DISCARD_NETWORK_CHANGED => C::NetworkChanged,
        abi::MC_DISCARD_DEVICE_WAS_ASLEEP => C::DeviceWasAsleep,
        abi::MC_DISCARD_CAPTIVE_PORTAL => C::CaptivePortal,
        abi::MC_DISCARD_FOREIGN_TUNNEL => C::ForeignTunnelDetected,
        other => return Err(Fail::invalid(format!("нет причины выброса с кодом {other}"))),
    })
}

/// Экспозиция маршрута из кода и узла.
pub fn exposure_from(code: i32, node: &str) -> Result<Exposure, Fail> {
    Ok(match code {
        abi::MC_EXPOSURE_TUNNELLED => {
            if node.is_empty() {
                return Err(Fail::invalid("туннельная экспозиция без узла выхода"));
            }
            Exposure::Tunnelled { node: NodeId::new(node) }
        }
        abi::MC_EXPOSURE_DIRECT => Exposure::Direct,
        abi::MC_EXPOSURE_BLOCKED => Exposure::Blocked,
        other => return Err(Fail::invalid(format!("нет экспозиции с кодом {other}"))),
    })
}

pub fn on_empty_from(code: i32, target: &str) -> Result<OnEmpty, Fail> {
    Ok(match code {
        abi::MC_ON_EMPTY_BLOCK => OnEmpty::Block,
        abi::MC_ON_EMPTY_HOLD_LAST => OnEmpty::HoldLast,
        abi::MC_ON_EMPTY_FALLBACK => {
            if target.is_empty() {
                return Err(Fail::invalid("запасная полоса выбрана, но не названа"));
            }
            OnEmpty::Fallback { lane: LaneId::new(target) }
        }
        other => return Err(Fail::invalid(format!("нет действия при пустоте с кодом {other}"))),
    })
}
