//! Сохранение и загрузка накопленной истории измерений.
//!
//! ## Зачем это вообще
//!
//! Без переноса истории каждый запуск приложения даёт две-три минуты, когда
//! все маршруты одинаково недостоверны: порог уверенности не пройден ни одним,
//! и выбор фактически случаен. Уверенность набирается не мгновенно —
//! `C_n = min(1, n/30)` и ширина интервала Уилсона требуют десятков проб.
//!
//! ## Что сохраняется, а что нет — и почему
//!
//! Сохраняется ТОЛЬКО здоровье маршрутов: перцентили, джиттер, потери,
//! полоса, счётчики, факты (подмена DNS, выход мимо узла). Это свойства
//! маршрута, и они переживают перезапуск честно.
//!
//! НЕ сохраняются:
//! - **состояние демпфера** (сглаженные оценки, штрафы за флап, остывание) —
//!   оно целиком выражено в монотонных часах, которые при перезапуске
//!   обнуляются. Восстановленный `switched_at` из чужой эпохи означал бы либо
//!   вечный запрет на переключение, либо мгновенное разрешение. Ядро по этой
//!   же причине не даёт `DamperState` сериализовать вовсе;
//! - **состояние полос** (текущий маршрут, закрепление) — его знает
//!   исполнитель, и только он знает, живо ли оно после перезапуска. Срок
//!   закрепления — те же монотонные часы;
//! - **каталог** маршрутов и полос — он приходит из подписки и объявляется
//!   заново при каждом запуске. Если бы каталог ехал в состоянии, старая
//!   запись пережила бы удаление ноды.
//!
//! ## Про время
//!
//! Все моменты внутри `RouteHealth` — монотонные часы платформы, обнуляющиеся
//! при перезагрузке. Поэтому в файл кладётся момент сохранения, а при загрузке
//! спрашивается, сколько реального времени прошло. Из этих двух чисел моменты
//! пересчитываются в НЫНЕШНЮЮ эпоху: иначе замер полугодовой давности выглядел
//! бы свежим (если старые часы были больше новых) или наоборот.
//!
//! Смысл переноса от этого не теряется: возраст замера бьёт по множителю
//! `C_age`, но число проб и ширина интервала Уилсона сохраняются. Значит,
//! ПЕРВАЯ же успешная проба после запуска возвращает маршруту полную
//! уверенность вместо шестидесяти проб с нуля. Ради этого всё и делается.

use std::collections::BTreeMap;

use method_core::ids::RouteId;
use method_core::metrics::RouteHealth;
use method_core::Instant;
use serde::{Deserialize, Serialize};

use crate::engine::Engine;
use crate::error::{Fail, Status};

/// Версия формата. Растёт при любом несовместимом изменении; чужую версию
/// читать не пытаемся — лучше начать с чистой истории, чем додумать поля.
pub const STATE_VERSION: u32 = 1;

#[derive(Serialize, Deserialize)]
pub struct SavedRoute {
    pub health: RouteHealth,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub throughput_at_ms: Option<u64>,
}

#[derive(Serialize, Deserialize)]
pub struct SavedState {
    pub v: u32,
    /// Момент сохранения по тем же монотонным часам, что и всё остальное.
    pub saved_at_ms: u64,
    /// Шаг проб на момент сохранения. Не применяется при загрузке (он задан
    /// при создании движка), но лежит рядом: без него числа в файле
    /// невозможно истолковать при разборе руками.
    pub probe_interval_ms: u64,
    pub routes: BTreeMap<String, SavedRoute>,
}

/// Сериализовать историю. JSON, а не свой двоичный формат: объём — десятки
/// килобайт, зато файл читается глазами при разборе жалобы «почему он выбрал
/// эту ноду», и версия формата видна без инструментов.
pub fn save(engine: &Engine, now: Instant) -> Result<Vec<u8>, Fail> {
    let mut routes = BTreeMap::new();
    for (id, health, tp) in engine.health_entries() {
        routes.insert(
            id.as_str().to_owned(),
            SavedRoute { health: health.clone(), throughput_at_ms: tp.map(|t| t.0) },
        );
    }
    let state = SavedState {
        v: STATE_VERSION,
        saved_at_ms: now.0,
        probe_interval_ms: engine.probe_interval_ms(),
        routes,
    };
    serde_json::to_vec(&state)
        .map_err(|e| Fail::new(Status::Internal, format!("состояние не сериализуется: {e}")))
}

/// Разобрать и влить историю.
///
/// `elapsed_ms` — сколько РЕАЛЬНОГО времени прошло между сохранением и
/// загрузкой. Это единственное, чего библиотека не может узнать сама: часов
/// она не читает принципиально (иначе решения перестают быть чистой функцией
/// от входа и не проверяются на записанном логе). Приложение берёт разницу по
/// стенным часам; ноль допустим и означает «сохранено только что».
pub fn load(
    engine: &mut Engine,
    bytes: &[u8],
    now: Instant,
    elapsed_ms: u64,
) -> Result<(), Fail> {
    let state: SavedState = serde_json::from_slice(bytes).map_err(|e| {
        Fail::new(Status::StateInvalid, format!("состояние не разбирается: {e}"))
    })?;
    if state.v != STATE_VERSION {
        return Err(Fail::new(
            Status::StateInvalid,
            format!(
                "версия состояния {} не совпадает с нынешней {STATE_VERSION}",
                state.v
            ),
        ));
    }

    for (id, saved) in state.routes {
        let mut health = saved.health;
        rebase(&mut health, state.saved_at_ms, now.0, elapsed_ms);
        let tp = saved
            .throughput_at_ms
            .map(|t| rebase_instant(t, state.saved_at_ms, now.0, elapsed_ms))
            .unwrap_or(None);
        engine.restore_health(RouteId::new(id), health, tp);
    }
    Ok(())
}

/// Пересчитать момент из прошлой эпохи монотонных часов в нынешнюю.
///
/// `None` означает «замер настолько стар, что момента у него больше нет».
/// Именно `None`, а не ноль: ноль в свежезагруженной системе, где `now` тоже
/// мал, выглядел бы совсем недавним замером. `RouteHealth` с пустым
/// `last_sample_at` даёт уверенность ровно ноль — маршрут не участвует в
/// выборе, пока его не измерят снова, и это ровно то, что мы хотим сказать.
fn rebase_instant(v: u64, saved_at: u64, now: u64, elapsed: u64) -> Option<Instant> {
    let age = saved_at.saturating_sub(v).saturating_add(elapsed);
    if age > now {
        return None;
    }
    Some(Instant(now - age))
}

fn rebase(h: &mut RouteHealth, saved_at: u64, now: u64, elapsed: u64) {
    if let Some(Instant(v)) = h.last_sample_at {
        h.last_sample_at = rebase_instant(v, saved_at, now, elapsed);
    }
    if let Some(Instant(v)) = h.first_sample_at {
        h.first_sample_at = rebase_instant(v, saved_at, now, elapsed);
    }
    // Если свежести не осталось у последнего замера, то и у первого её нет:
    // держать `first` без `last` бессмысленно и путает при разборе.
    if h.last_sample_at.is_none() {
        h.first_sample_at = None;
    }
}
