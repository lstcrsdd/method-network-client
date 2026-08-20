//! Сама граница: единственный файл, где есть `unsafe`, сырые указатели и
//! `extern "C"`.
//!
//! Три правила, которым подчинена каждая функция ниже.
//!
//! **1. Паника не имеет права пересечь границу.** Разворачивание паники через
//! кадры C — неопределённое поведение: чужие кадры не знают про деструкторы
//! Rust, и в лучшем случае процесс падает, в худшем продолжает работать с
//! испорченным стеком. Поэтому тело каждой экспортируемой функции обёрнуто в
//! `catch_unwind`, а пойманная паника превращается в код `MC_PANIC`. После неё
//! движок помечается отравленным: паника случилась ГДЕ-ТО В СЕРЕДИНЕ операции,
//! и продолжать с ополовиненным состоянием опаснее, чем отказать.
//!
//! **2. Владение однозначно.** Всё, что библиотека выдала наружу, освобождается
//! только парной функцией отсюда же: движок — `mc_engine_free`, решение —
//! `mc_decision_free`, буфер — `mc_buffer_free`. Строки наружу не выдаются во
//! владение НИКОГДА: они всегда заимствованы у названного объекта и живут
//! ровно столько же, сколько он.
//!
//! **3. Через границу идут только простые типы.** Целые, указатели, длины и
//! плоские `#[repr(C)]`-структуры. Ни одного типа Rust со сложной раскладкой.

use std::cell::UnsafeCell;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, Ordering};

use method_core::axis::ExposureSet;
use method_core::ids::NodeId;
use method_core::lane::{Hysteresis, RouteRequirements};
use method_core::metrics::ProbeOutcome;
use method_core::route::Carries;
use method_core::Instant;

use crate::abi::*;
use crate::engine::{self, Decision, Engine, LaneInput, RouteInput};
use crate::error::{fail, finish, with_last_error, Fail, Status};
use crate::state;

/// Версия ABI. Растёт при любом изменении, ломающем совместимость с
/// заголовком: перестановке полей, смене значений констант, смене сигнатуры.
pub const MC_ABI_VERSION: u32 = 1;

// ─────────────────────────── Непрозрачные объекты ───────────────────────────

/// Метки для проверки, что нам передали то, что мы выдавали.
///
/// Защита не полная — по освобождённому указателю чтение и так неопределённое
/// поведение, — но на практике ловит и двойное освобождение, и перепутанные
/// местами объекты, и мусор в поле структуры Swift. Стоит одно сравнение.
const ENGINE_MAGIC: u64 = 0x4d43_5f45_4e47_5630;
const DECISION_MAGIC: u64 = 0x4d43_5f44_4543_5630;

/// Оболочка движка: метка, флаг занятости, флаг отравления и сам движок.
///
/// `UnsafeCell` потому, что C передаёт нам `*mut`, и никакой изменяемой ссылки
/// с проверяемым временем жизни здесь быть не может по определению.
pub struct McEngine {
    magic: u64,
    /// Движок НЕ `Sync`: внутри демпфера живёт `Cell`. Вместо того чтобы
    /// написать в документации «не вызывайте из двух потоков» и надеяться,
    /// граница ловит пересечение сама и возвращает `MC_BUSY`. Обмен с
    /// `Acquire`/`Release` заодно даёт нужный порядок памяти между
    /// последовательными вызовами из РАЗНЫХ потоков — так что чередующийся
    /// доступ (например, из очереди Swift) безопасен, а одновременный
    /// превращается из гонки в честную ошибку.
    busy: AtomicBool,
    poisoned: AtomicBool,
    inner: UnsafeCell<Engine>,
}

/// Решение: список действий и журнал причин. Строки внутри — источник тех
/// указателей, что уезжают в `McAction::reason` и `McReason::text`.
pub struct McDecision {
    magic: u64,
    decision: Decision,
}

// ──────────────────────────── Служебное ────────────────────────────

struct BusyGuard<'a>(&'a McEngine);

impl Drop for BusyGuard<'_> {
    fn drop(&mut self) {
        self.0.busy.store(false, Ordering::Release);
    }
}

fn null(field: &str) -> Fail {
    Fail::new(Status::NullPointer, format!("аргумент «{field}» равен NULL"))
}

// ПРАВИЛО ВЫХОДНЫХ ПАРАМЕТРОВ: они заполняются ТОЛЬКО при успехе.
//
// Соблазн обнулить их заранее велик — и он же ловушка. Неудачный вызов,
// затирающий переменную вызывающего, отнимает у него единственную ссылку на
// то, что там лежало. Именно так утёк движок в первом прогоне теста на C:
// `mc_engine_new(0, &e)` отверг нулевой шаг проб — и обнулил `e`, в котором
// лежал прежний, вполне живой движок. Своей памяти библиотека при этом не
// теряла: терял вызывающий, и не по своей вине.

/// Обёртка для функций, которым движок не нужен.
fn shield<F: FnOnce() -> Result<(), Fail>>(f: F) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(r) => finish(r),
        Err(_) => fail(Fail::new(
            Status::Panic,
            "паника внутри библиотеки поймана на границе; наружу она не ушла",
        )),
    }
}

/// Обёртка для функций, работающих с движком: проверка метки, отравления,
/// занятости — и `catch_unwind` вокруг самой работы.
///
/// # Safety
/// `ptr` — либо NULL, либо значение, выданное `mc_engine_new` и ещё не
/// освобождённое.
unsafe fn with_engine<F>(ptr: *mut McEngine, f: F) -> i32
where
    F: FnOnce(&mut Engine) -> Result<(), Fail>,
{
    if ptr.is_null() {
        return fail(null("engine"));
    }
    let handle = &*ptr;
    if handle.magic != ENGINE_MAGIC {
        return fail(Fail::new(
            Status::InvalidHandle,
            "переданный указатель не похож на движок (метка не совпала)",
        ));
    }
    if handle.poisoned.load(Ordering::Acquire) {
        return fail(Fail::new(
            Status::Poisoned,
            "движок отравлен предыдущей паникой; создай новый и загрузи состояние",
        ));
    }
    if handle
        .busy
        .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
        .is_err()
    {
        return fail(Fail::new(
            Status::Busy,
            "движок уже занят вызовом из другого потока: сериализуй доступ, а не повторяй в цикле",
        ));
    }
    let _guard = BusyGuard(handle);

    let engine = &mut *handle.inner.get();
    match catch_unwind(AssertUnwindSafe(|| f(engine))) {
        Ok(r) => finish(r),
        Err(_) => {
            // Паника случилась посреди операции: часть структур уже изменена,
            // часть нет. Дальше пользоваться этим движком нельзя.
            handle.poisoned.store(true, Ordering::Release);
            fail(Fail::new(
                Status::Panic,
                "паника внутри ядра поймана на границе; движок отравлен и больше не принимает вызовов",
            ))
        }
    }
}

/// # Safety
/// `ptr` — либо NULL, либо значение из `mc_engine_reconcile`.
unsafe fn decision_ref<'a>(ptr: *const McDecision) -> Result<&'a McDecision, Fail> {
    if ptr.is_null() {
        return Err(null("decision"));
    }
    let d = &*ptr;
    if d.magic != DECISION_MAGIC {
        return Err(Fail::new(
            Status::InvalidHandle,
            "переданный указатель не похож на решение (метка не совпала)",
        ));
    }
    Ok(d)
}

// ──────────────────────────── Версия и ошибки ────────────────────────────

/// Версия ABI.
///
/// Единственная функция, возвращающая значение напрямую: она не может
/// провалиться, а ноль версией не бывает — двусмысленности нет.
#[no_mangle]
pub extern "C" fn mc_abi_version() -> u32 {
    MC_ABI_VERSION
}

/// Текст последней ошибки ЭТОГО потока.
///
/// Указывает во внутренний буфер потока и действителен до следующего вызова
/// любой функции библиотеки из этого же потока. Освобождать не нужно и нельзя.
///
/// # Safety
/// `out` — указатель на `mc_str_t`, в который можно писать.
#[no_mangle]
pub unsafe extern "C" fn mc_last_error(out: *mut McStr) -> i32 {
    if out.is_null() {
        // Здесь `fail` не зовём: он бы затёр то самое сообщение, за которым
        // пришли.
        return Status::NullPointer.code();
    }
    let s = with_last_error(|s| McStr { ptr: s.as_ptr(), len: s.len() });
    out.write(s);
    Status::Ok.code()
}

/// Статический текст кода возврата. Освобождать не нужно: строка живёт
/// столько же, сколько сама библиотека.
///
/// # Safety
/// `out` — указатель на `mc_str_t`, в который можно писать.
#[no_mangle]
pub unsafe extern "C" fn mc_status_text(code: i32, out: *mut McStr) -> i32 {
    if out.is_null() {
        return Status::NullPointer.code();
    }
    out.write(McStr::borrow(Status::text(code)));
    Status::Ok.code()
}

/// Нарочно паникует внутри границы — и обязана вернуть `MC_PANIC`.
///
/// Существует только под флагом сборки `test-panic` и в продукт не попадает.
/// Нужна затем, что профиль тестов Rust всегда разворачивает панику, а
/// проверять надо ту сборку, которая поедет в приложение: если в профиле
/// стоит `panic = "abort"`, `catch_unwind` не поймает ничего, и выяснится это
/// на устройстве человека.
#[cfg(feature = "test-panic")]
#[no_mangle]
pub extern "C" fn mc_test_panic() -> i32 {
    // Сообщение паники в вывод не пускаем: тест как раз о том, что она поймана.
    let prev = std::panic::take_hook();
    std::panic::set_hook(Box::new(|_| {}));
    let rc = shield(|| panic!("нарочная паника для проверки границы"));
    std::panic::set_hook(prev);
    rc
}

// ──────────────────────────── Жизнь движка ────────────────────────────

/// Создать движок.
///
/// # Safety
/// `out` — указатель на переменную под указатель движка.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_new(probe_interval_ms: u64, out: *mut *mut McEngine) -> i32 {
    shield(|| {
        if out.is_null() {
            return Err(null("out"));
        }
        let engine = Engine::new(probe_interval_ms)?;
        let boxed = Box::new(McEngine {
            magic: ENGINE_MAGIC,
            busy: AtomicBool::new(false),
            poisoned: AtomicBool::new(false),
            inner: UnsafeCell::new(engine),
        });
        out.write(Box::into_raw(boxed));
        Ok(())
    })
}

/// Уничтожить движок. NULL допустим и ничего не делает.
///
/// # Safety
/// Указатель должен быть выдан `mc_engine_new` и не освобождён ранее. Вызывать
/// в момент, когда другой поток находится внутри вызова с этим же движком,
/// нельзя — от этого не защищает никакая метка.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_free(engine: *mut McEngine) {
    if engine.is_null() {
        return;
    }
    if (*engine).magic != ENGINE_MAGIC {
        return;
    }
    // Метку затираем ДО освобождения: если тот же указатель придёт снова,
    // проверка метки поймает это, пока память ещё не переиспользована.
    (*engine).magic = 0;
    drop(Box::from_raw(engine));
}

// ──────────────────────────── Каталог ────────────────────────────

/// Заполнить описание маршрута умолчаниями.
///
/// Нужно не для удобства, а против тихой ошибки: обнулённая структура означала
/// бы «не несёт ни TCP, ни UDP, стоит на оси QUIC, экспозиция ноль» — набор,
/// который приходится ловить проверками. Здесь же умолчания взяты у ядра
/// (`Carries::default`), и в одном месте.
///
/// # Safety
/// `out` — указатель на `mc_route_desc_t`, в который можно писать.
#[no_mangle]
pub unsafe extern "C" fn mc_route_desc_init(out: *mut McRouteDesc) -> i32 {
    shield(|| {
        if out.is_null() {
            return Err(null("out"));
        }
        let c = Carries::default();
        out.write(McRouteDesc {
            id: McStr::empty(),
            node: McStr::empty(),
            transport: McStr::empty(),
            country: McStr::empty(),
            exposure_node: McStr::empty(),
            axis: MC_AXIS_NONE,
            exposure: MC_EXPOSURE_TUNNELLED,
            handshake_cost: MC_HANDSHAKE_CHEAP,
            carries_tcp: c.tcp as u8,
            carries_udp: c.udp as u8,
            carries_v4: c.v4 as u8,
            carries_v6: c.v6 as u8,
        });
        Ok(())
    })
}

/// Заполнить описание полосы умолчаниями.
///
/// Умолчания гистерезиса берутся у ядра (`Hysteresis::default`), а не пишутся
/// числами здесь: обнулённый гистерезис — это порог ноль, выдержка ноль и
/// остывание ноль, то есть дребезг на каждом замере. Такую ошибку не видно,
/// пока не посмотришь на график переключений за сутки.
///
/// # Safety
/// `out` — указатель на `mc_lane_desc_t`, в который можно писать.
#[no_mangle]
pub unsafe extern "C" fn mc_lane_desc_init(out: *mut McLaneDesc) -> i32 {
    shield(|| {
        if out.is_null() {
            return Err(null("out"));
        }
        let h = Hysteresis::default();
        out.write(McLaneDesc {
            id: McStr::empty(),
            title: McStr::empty(),
            justification: McStr::empty(),
            on_empty_lane: McStr::empty(),
            axis_in: std::ptr::null(),
            axis_in_len: 0,
            axis_not_in: std::ptr::null(),
            axis_not_in_len: 0,
            include_country: std::ptr::null(),
            include_country_len: 0,
            exclude_country: std::ptr::null(),
            exclude_country_len: 0,
            cooldown_ms: h.cooldown_ms,
            cooldown_max_ms: h.cooldown_max_ms,
            margin_floor: h.margin_floor,
            sla: MC_SLA_BROWSE,
            on_empty: MC_ON_EMPTY_BLOCK,
            switch_mode: MC_SWITCH_DRAIN,
            // Только через туннель: единственное умолчание, при котором
            // ошибка вызывающего не превращается в открытый трафик.
            allow: MC_EXPOSURE_TUNNELLED as u8,
            dwell: h.dwell,
            // Полоса без запаса на ДРУГОЙ оси не имеет запаса вообще.
            min_axes: 2,
            require_udp: 0,
            require_v6: 0,
        });
        Ok(())
    })
}

/// Объявить маршрут. Дескриптор действителен всё время жизни движка.
///
/// # Safety
/// `desc` — заполненное описание; строки внутри читаются только на время
/// вызова и копируются, так что после возврата их можно освобождать.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_add_route(
    engine: *mut McEngine,
    desc: *const McRouteDesc,
    out_route: *mut u32,
) -> i32 {
    with_engine(engine, |e| {
        if desc.is_null() {
            return Err(null("desc"));
        }
        if out_route.is_null() {
            return Err(null("out_route"));
        }
        let d = &*desc;

        let id = d.id.as_nonempty("id")?;
        let node = d.node.as_nonempty("node")?;
        let transport = d.transport.as_nonempty("transport")?;
        let country = d.country.as_str("country")?;
        let exposure_node = d.exposure_node.as_str("exposure_node")?;

        let axis = engine::axis_from(d.axis)?;
        let exposure = engine::exposure_from(
            d.exposure,
            if exposure_node.is_empty() { node } else { exposure_node },
        )?;
        let handshake_cost = engine::handshake_from(d.handshake_cost)?;

        let handle = e.add_route(RouteInput {
            id: id.to_owned(),
            node: node.to_owned(),
            transport: transport.to_owned(),
            country: country.to_owned(),
            axis,
            exposure,
            carries: Carries {
                tcp: d.carries_tcp != 0,
                udp: d.carries_udp != 0,
                v4: d.carries_v4 != 0,
                v6: d.carries_v6 != 0,
            },
            handshake_cost,
        })?;
        out_route.write(handle);
        Ok(())
    })
}

/// Объявить полосу.
///
/// # Safety
/// `desc` — заполненное описание; массивы и строки внутри читаются только на
/// время вызова.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_add_lane(
    engine: *mut McEngine,
    desc: *const McLaneDesc,
    out_lane: *mut u32,
) -> i32 {
    with_engine(engine, |e| {
        if desc.is_null() {
            return Err(null("desc"));
        }
        if out_lane.is_null() {
            return Err(null("out_lane"));
        }
        let d = &*desc;

        let id = d.id.as_nonempty("id")?;
        let title = d.title.as_str("title")?;
        let justification = d.justification.as_str("justification")?;
        let on_empty_lane = d.on_empty_lane.as_str("on_empty_lane")?;

        // NULL — «без ограничения». Непустой массив — «разрешено ровно это».
        // А вот НЕнулевой указатель при нулевой длине почти наверняка описка:
        // пустой белый список запрещает всё, и полоса молча осталась бы без
        // кандидатов. Отказываем словами, чтобы это не искали в логах.
        if !d.axis_in.is_null() && d.axis_in_len == 0 {
            return Err(Fail::invalid(
                "пустой белый список осей запретил бы всё; для «без ограничения» передавай NULL",
            ));
        }
        if !d.include_country.is_null() && d.include_country_len == 0 {
            return Err(Fail::invalid(
                "пустой белый список стран запретил бы всё; для «без ограничения» передавай NULL",
            ));
        }
        let axis_in = if d.axis_in.is_null() {
            None
        } else {
            let raw = slice_from(d.axis_in, d.axis_in_len, "axis_in")?;
            Some(raw.iter().map(|c| engine::axis_from(*c)).collect::<Result<Vec<_>, _>>()?)
        };
        let axis_not_in = slice_from(d.axis_not_in, d.axis_not_in_len, "axis_not_in")?
            .iter()
            .map(|c| engine::axis_from(*c))
            .collect::<Result<Vec<_>, _>>()?;

        let include_country = if d.include_country.is_null() {
            None
        } else {
            let raw = slice_from(d.include_country, d.include_country_len, "include_country")?;
            Some(
                raw.iter()
                    .map(|s| s.as_nonempty("include_country[]").map(str::to_owned))
                    .collect::<Result<Vec<_>, _>>()?,
            )
        };
        let exclude_country = slice_from(d.exclude_country, d.exclude_country_len, "exclude_country")?
            .iter()
            .map(|s| s.as_nonempty("exclude_country[]").map(str::to_owned))
            .collect::<Result<Vec<_>, _>>()?;

        let handle = e.add_lane(LaneInput {
            id: id.to_owned(),
            title: if title.is_empty() { id.to_owned() } else { title.to_owned() },
            sla: engine::sla_from(d.sla)?,
            allow: ExposureSet(d.allow),
            justification: (!justification.trim().is_empty()).then(|| justification.to_owned()),
            need: RouteRequirements {
                axis_in,
                axis_not_in,
                exclude_country,
                include_country,
                require_udp: d.require_udp != 0,
                require_v6: d.require_v6 != 0,
            },
            min_axes: d.min_axes,
            on_empty: engine::on_empty_from(d.on_empty, on_empty_lane)?,
            switch: engine::switch_from(d.switch_mode)?,
            hysteresis: Hysteresis {
                margin_floor: d.margin_floor,
                dwell: d.dwell,
                cooldown_ms: d.cooldown_ms,
                cooldown_max_ms: d.cooldown_max_ms,
            },
        })?;
        out_lane.write(handle);
        Ok(())
    })
}

/// Сколько маршрутов объявлено.
///
/// # Safety
/// `out` — указатель на `size_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_route_count(engine: *mut McEngine, out: *mut usize) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        out.write(e.route_count());
        Ok(())
    })
}

/// Сколько полос объявлено.
///
/// # Safety
/// `out` — указатель на `size_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_lane_count(engine: *mut McEngine, out: *mut usize) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        out.write(e.lane_count());
        Ok(())
    })
}

/// Дескриптор маршрута по строковому идентификатору.
///
/// # Safety
/// `id` — строка UTF-8 длиной `id.len`; `out` — указатель на `uint32_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_route_handle(
    engine: *mut McEngine,
    id: McStr,
    out: *mut u32,
) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        let handle = e.route_handle(id.as_nonempty("id")?)?;
        out.write(handle);
        Ok(())
    })
}

/// Дескриптор полосы по строковому идентификатору.
///
/// # Safety
/// См. [`mc_engine_route_handle`].
#[no_mangle]
pub unsafe extern "C" fn mc_engine_lane_handle(
    engine: *mut McEngine,
    id: McStr,
    out: *mut u32,
) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        let handle = e.lane_handle(id.as_nonempty("id")?)?;
        out.write(handle);
        Ok(())
    })
}

/// Строковый идентификатор маршрута. Строка заимствована у движка и живёт,
/// пока жив движок.
///
/// # Safety
/// `out` — указатель на `mc_str_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_route_id(
    engine: *mut McEngine,
    route: u32,
    out: *mut McStr,
) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        out.write(McStr::borrow(e.route_id(route)?));
        Ok(())
    })
}

/// Строковый идентификатор полосы. Заимствован у движка.
///
/// # Safety
/// `out` — указатель на `mc_str_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_lane_id(
    engine: *mut McEngine,
    lane: u32,
    out: *mut McStr,
) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        out.write(McStr::borrow(e.lane_id(lane)?));
        Ok(())
    })
}

// ──────────────────────────── Измерения ────────────────────────────

/// Подать пробу.
///
/// # Safety
/// `probe` — заполненная структура; строка `got_node` читается только на время
/// вызова.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_observe(engine: *mut McEngine, probe: *const McProbe) -> i32 {
    with_engine(engine, |e| {
        if probe.is_null() {
            return Err(null("probe"));
        }
        let p = &*probe;
        let outcome = match p.outcome {
            MC_OUTCOME_OK => {
                if !p.rtt_ms.is_finite() || p.rtt_ms < 0.0 {
                    return Err(Fail::invalid(
                        "успешная проба обязана нести конечную неотрицательную задержку",
                    ));
                }
                ProbeOutcome::Ok { rtt_ms: p.rtt_ms }
            }
            MC_OUTCOME_TIMEOUT => ProbeOutcome::Timeout,
            MC_OUTCOME_HANDSHAKE_FAILED => ProbeOutcome::HandshakeFailed,
            MC_OUTCOME_EXIT_MISMATCH => {
                let got = p.got_node.as_str("got_node")?;
                // Ожидаемым узлом всегда является узел самого маршрута: если
                // трафик должен был выйти где-то ещё, это другой маршрут.
                // Пустое «куда вышло» — не ноль и не пустая строка внутри
                // ядра, а честное `None`: «вышло мимо, куда — не знаем».
                ProbeOutcome::ExitMismatch {
                    expected: NodeId::new(""),
                    got: (!got.is_empty()).then(|| NodeId::new(got)),
                }
            }
            MC_OUTCOME_DNS_TAMPERED => ProbeOutcome::DnsTampered,
            MC_OUTCOME_DISCARDED => ProbeOutcome::Discarded {
                cause: engine::discard_cause_from(p.cause)?,
            },
            other => return Err(Fail::invalid(format!("нет исхода пробы с кодом {other}"))),
        };
        e.observe(p.route, Instant(p.at_ms), outcome)
    })
}

/// Подать замер полосы пропускания.
///
/// Отдельно от пробы намеренно: полосу мерят редко и дорого, а задержку —
/// часто и дёшево. `None` в полосе означает НЕ ИЗМЕРЕНО, и это не ноль: иначе
/// выбор для загрузок делался бы по задержке под видом выбора по скорости.
///
/// # Safety
/// Движок должен быть действителен.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_observe_throughput(
    engine: *mut McEngine,
    route: u32,
    at_ms: u64,
    mbps: f32,
) -> i32 {
    with_engine(engine, |e| e.observe_throughput(route, Instant(at_ms), mbps))
}

/// Сеть сменилась: забыть всё накопленное, каталог оставить.
///
/// # Safety
/// Движок должен быть действителен.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_network_changed(engine: *mut McEngine) -> i32 {
    with_engine(engine, |e| {
        e.network_changed();
        Ok(())
    })
}

// ──────────────────────────── Решение ────────────────────────────

/// Свести желаемое с действительным на момент `now_ms`.
///
/// Движок НИЧЕГО не исполняет и не считает исполненным. Пока исполнитель не
/// подтвердил применение через `mc_engine_lane_applied`, полоса для движка
/// по-прежнему без маршрута — и следующий `reconcile` предложит выбор снова,
/// начислив маршруту очередной штраф за дребезг. Это не придирка: подтверждать
/// надо ровно тогда, когда переключение действительно состоялось.
///
/// # Safety
/// `out` — указатель на переменную под указатель решения. Полученное решение
/// освобождается `mc_decision_free`.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_reconcile(
    engine: *mut McEngine,
    now_ms: u64,
    out: *mut *mut McDecision,
) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        let decision = e.reconcile(Instant(now_ms));
        let boxed = Box::new(McDecision { magic: DECISION_MAGIC, decision });
        out.write(Box::into_raw(boxed));
        Ok(())
    })
}

/// Сколько действий в решении. Ноль — валидный ответ: движок решил ничего не
/// менять. Именно поэтому счётчик едет через выходной параметр, а не через
/// возвращаемое значение.
///
/// # Safety
/// `decision` — из `mc_engine_reconcile`; `out` — указатель на `size_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_decision_action_count(
    decision: *const McDecision,
    out: *mut usize,
) -> i32 {
    shield(|| {
        if out.is_null() {
            return Err(null("out"));
        }
        out.write(decision_ref(decision)?.decision.actions.len());
        Ok(())
    })
}

/// Одно действие. Строка причины заимствована у решения и умирает вместе с ним.
///
/// # Safety
/// `out` — указатель на `mc_action_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_decision_action(
    decision: *const McDecision,
    index: usize,
    out: *mut McAction,
) -> i32 {
    shield(|| {
        if out.is_null() {
            return Err(null("out"));
        }
        let d = decision_ref(decision)?;
        let a = d.decision.actions.get(index).ok_or_else(|| {
            Fail::invalid(format!(
                "действия с номером {index} нет: их всего {}",
                d.decision.actions.len()
            ))
        })?;
        out.write(McAction {
            kind: a.kind,
            lane: a.lane,
            route: a.route,
            on_empty: a.on_empty,
            on_empty_lane: a.on_empty_lane,
            reason_kind: a.reason_kind,
            reason: McStr::borrow(&a.text),
        });
        Ok(())
    })
}

/// Сколько записей в журнале причин.
///
/// # Safety
/// `out` — указатель на `size_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_decision_reason_count(
    decision: *const McDecision,
    out: *mut usize,
) -> i32 {
    shield(|| {
        if out.is_null() {
            return Err(null("out"));
        }
        out.write(decision_ref(decision)?.decision.reasons.len());
        Ok(())
    })
}

/// Одна запись журнала. Журнал шире списка действий: в нём есть и то, почему
/// движок НЕ стал переключаться, — а это человеку интереснее прочего.
///
/// # Safety
/// `out` — указатель на `mc_reason_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_decision_reason(
    decision: *const McDecision,
    index: usize,
    out: *mut McReason,
) -> i32 {
    shield(|| {
        if out.is_null() {
            return Err(null("out"));
        }
        let d = decision_ref(decision)?;
        let r = d.decision.reasons.get(index).ok_or_else(|| {
            Fail::invalid(format!(
                "причины с номером {index} нет: их всего {}",
                d.decision.reasons.len()
            ))
        })?;
        out.write(McReason { kind: r.kind, lane: r.lane, text: McStr::borrow(&r.text) });
        Ok(())
    })
}

/// Освободить решение. Все выданные из него строки после этого недействительны.
/// NULL допустим.
///
/// # Safety
/// Указатель должен быть выдан `mc_engine_reconcile` и не освобождён ранее.
#[no_mangle]
pub unsafe extern "C" fn mc_decision_free(decision: *mut McDecision) {
    if decision.is_null() {
        return;
    }
    if (*decision).magic != DECISION_MAGIC {
        return;
    }
    (*decision).magic = 0;
    drop(Box::from_raw(decision));
}

// ──────────────────────────── Состояние полос ────────────────────────────

/// Подтвердить, что маршрут действительно поставлен полосе.
///
/// # Safety
/// Движок должен быть действителен.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_lane_applied(
    engine: *mut McEngine,
    lane: u32,
    route: u32,
) -> i32 {
    with_engine(engine, |e| e.lane_applied(lane, route))
}

/// Сообщить, что полоса осталась без маршрута (блокировка, обрыв, отказ
/// применения).
///
/// # Safety
/// Движок должен быть действителен.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_lane_cleared(engine: *mut McEngine, lane: u32) -> i32 {
    with_engine(engine, |e| e.lane_cleared(lane))
}

/// Какой маршрут стоит у полосы сейчас. Ноль означает «никакой» — дескриптором
/// ноль не бывает, поэтому двусмысленности нет.
///
/// # Safety
/// `out` — указатель на `uint32_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_lane_current(
    engine: *mut McEngine,
    lane: u32,
    out: *mut u32,
) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        out.write(e.lane_current(lane)?);
        Ok(())
    })
}

/// Закрепить маршрут за полосой до момента `until_ms`.
///
/// Закрепление сильнее всей математики, но не сильнее фактов: мёртвый или
/// запрещённый политикой маршрут движок с полосы всё равно снимет и скажет об
/// этом словами.
///
/// # Safety
/// Движок должен быть действителен.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_lane_pin(
    engine: *mut McEngine,
    lane: u32,
    route: u32,
    until_ms: u64,
) -> i32 {
    with_engine(engine, |e| e.lane_pin(lane, route, Instant(until_ms)))
}

/// Снять закрепление.
///
/// # Safety
/// Движок должен быть действителен.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_lane_unpin(engine: *mut McEngine, lane: u32) -> i32 {
    with_engine(engine, |e| e.lane_unpin(lane))
}

// ──────────────────────────── Оценка ────────────────────────────

/// Оценка маршрута под класс нагрузки.
///
/// # Safety
/// `out` — указатель на `mc_score_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_score(
    engine: *mut McEngine,
    route: u32,
    sla: i32,
    now_ms: u64,
    out: *mut McScore,
) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        let sla = engine::sla_from(sla)?;
        out.write(e.score(route, sla, Instant(now_ms))?);
        Ok(())
    })
}

// ──────────────────────── Сохранение и загрузка ────────────────────────

/// Выгрузить историю измерений.
///
/// Буфер освобождается `mc_buffer_free` и ничем иным.
///
/// # Safety
/// `out` — указатель на `mc_buffer_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_save_state(
    engine: *mut McEngine,
    now_ms: u64,
    out: *mut McBuffer,
) -> i32 {
    with_engine(engine, |e| {
        if out.is_null() {
            return Err(null("out"));
        }
        let bytes = state::save(e, Instant(now_ms))?;
        // `Box<[u8]>` вместо `Vec`: у среза нет ёмкости, значит наружу не надо
        // отдавать третье число и надеяться, что его вернут неизменным.
        let boxed = bytes.into_boxed_slice();
        let len = boxed.len();
        let data = Box::into_raw(boxed) as *mut u8;
        out.write(McBuffer { data, len });
        Ok(())
    })
}

/// Загрузить историю измерений.
///
/// `elapsed_ms` — сколько реального времени прошло между сохранением и этой
/// загрузкой (по стенным часам приложения; ноль означает «только что»).
/// Библиотека часов не читает принципиально, поэтому спрашивает.
///
/// # Safety
/// `data` — буфер длиной `len` байт.
#[no_mangle]
pub unsafe extern "C" fn mc_engine_load_state(
    engine: *mut McEngine,
    data: *const u8,
    len: usize,
    now_ms: u64,
    elapsed_ms: u64,
) -> i32 {
    with_engine(engine, |e| {
        let bytes = slice_from(data, len, "data")?;
        if bytes.is_empty() {
            return Err(Fail::new(Status::StateInvalid, "пустое состояние нечего загружать"));
        }
        state::load(e, bytes, Instant(now_ms), elapsed_ms)
    })
}

/// Освободить буфер и обнулить структуру. NULL допустим.
///
/// # Safety
/// `buffer` — структура, заполненная `mc_engine_save_state` и не освобождённая
/// ранее.
#[no_mangle]
pub unsafe extern "C" fn mc_buffer_free(buffer: *mut McBuffer) {
    if buffer.is_null() {
        return;
    }
    let b = *buffer;
    // Обнуляем ДО освобождения: повторный вызов на той же структуре тогда
    // безобиден, а не двойное освобождение.
    buffer.write(McBuffer::empty());
    if b.data.is_null() || b.len == 0 {
        return;
    }
    drop(Box::from_raw(std::slice::from_raw_parts_mut(b.data, b.len)));
}

// ──────────────────────────────── Тесты ────────────────────────────────
//
// Тест на C (`tests/abi.c`) проверяет раскладку и заголовок — то, чего Rust о
// себе не расскажет. Здесь проверяется другое: поведение самой границы —
// ловля паники, отравление, занятость и перенос состояния. Эти свойства
// нужно держать в `cargo test`, иначе они проверяются только тогда, когда
// кто-то вспомнит запустить скрипт.

#[cfg(test)]
mod tests {
    use super::*;

    /// Движок с одним маршрутом. Возвращает указатель и дескриптор.
    unsafe fn engine_with_route() -> (*mut McEngine, u32) {
        let mut e: *mut McEngine = std::ptr::null_mut();
        assert_eq!(mc_engine_new(5000, &mut e), 0);
        let mut d = std::mem::zeroed::<McRouteDesc>();
        assert_eq!(mc_route_desc_init(&mut d), 0);
        d.id = McStr::borrow("lt.trojan.8443");
        d.node = McStr::borrow("lt");
        d.transport = McStr::borrow("trojan");
        d.country = McStr::borrow("LT");
        d.axis = MC_AXIS_REAL_TLS;
        let mut route = 0u32;
        assert_eq!(mc_engine_add_route(e, &d, &mut route), 0);
        (e, route)
    }

    unsafe fn probe(e: *mut McEngine, route: u32, at: u64, rtt: f32) {
        let p = McProbe {
            route,
            outcome: MC_OUTCOME_OK,
            cause: 0,
            rtt_ms: rtt,
            at_ms: at,
            got_node: McStr::empty(),
        };
        assert_eq!(mc_engine_observe(e, &p), 0);
    }

    /// Раскладка структур пришпилена числами по ОБЕ стороны границы: те же
    /// числа стоят в `_Static_assert` внутри `tests/abi.c`. Если поле переедет
    /// или сменит тип, сломается одна из двух сторон — а значит, сломается
    /// раньше, чем Swift начнёт читать мусор из чужого смещения.
    #[test]
    fn раскладка_структур_пришпилена() {
        use std::mem::size_of;
        assert_eq!(size_of::<McStr>(), 16);
        assert_eq!(size_of::<McBuffer>(), 16);
        assert_eq!(size_of::<McRouteDesc>(), 96);
        assert_eq!(size_of::<McLaneDesc>(), 168);
        assert_eq!(size_of::<McProbe>(), 40);
        assert_eq!(size_of::<McAction>(), 40);
        assert_eq!(size_of::<McReason>(), 24);
        assert_eq!(size_of::<McScore>(), 32);
    }

    #[test]
    fn паника_не_уходит_наружу_и_травит_движок() {
        unsafe {
            let (e, _) = engine_with_route();

            // Сообщение паники в вывод теста не пускаем: оно выглядит как
            // упавший тест, хотя тест как раз про то, что паника поймана.
            let prev = std::panic::take_hook();
            std::panic::set_hook(Box::new(|_| {}));
            let rc = with_engine(e, |_| panic!("нарочно"));
            std::panic::set_hook(prev);

            assert_eq!(rc, Status::Panic.code(), "паника обязана стать кодом");

            // Дальше движок не принимает ничего: состояние изменено наполовину.
            let mut n = 0usize;
            assert_eq!(mc_engine_route_count(e, &mut n), Status::Poisoned.code());

            // Но освободить его по-прежнему можно — иначе утечка.
            mc_engine_free(e);
        }
    }

    #[test]
    fn одновременный_доступ_превращается_в_ошибку_а_не_в_гонку() {
        unsafe {
            let (e, _) = engine_with_route();
            let rc = with_engine(e, |_| {
                let mut n = 0usize;
                // Вложенный вызов — та же ситуация, что вызов из второго
                // потока: движок уже занят.
                assert_eq!(mc_engine_route_count(e, &mut n), Status::Busy.code());
                Ok(())
            });
            assert_eq!(rc, 0);

            // Занятость снимается даже после ошибки внутри.
            let mut n = 0usize;
            assert_eq!(mc_engine_route_count(e, &mut n), 0);
            assert_eq!(n, 1);
            mc_engine_free(e);
        }
    }

    #[test]
    fn занятость_снимается_и_после_паники() {
        unsafe {
            let (e, _) = engine_with_route();
            let prev = std::panic::take_hook();
            std::panic::set_hook(Box::new(|_| {}));
            assert_eq!(with_engine(e, |_| panic!("нарочно")), Status::Panic.code());
            std::panic::set_hook(prev);
            // Если бы сторож занятости не отработал при развороте стека, ниже
            // был бы MC_BUSY, а не MC_POISONED.
            let mut n = 0usize;
            assert_eq!(mc_engine_route_count(e, &mut n), Status::Poisoned.code());
            mc_engine_free(e);
        }
    }

    #[test]
    fn чужой_указатель_не_принимается_за_движок() {
        unsafe {
            let mut garbage: u64 = 0xdead_beef;
            let ptr = (&mut garbage as *mut u64).cast::<McEngine>();
            let mut n = 0usize;
            assert_eq!(mc_engine_route_count(ptr, &mut n), Status::InvalidHandle.code());
            assert_eq!(mc_engine_route_count(std::ptr::null_mut(), &mut n), Status::NullPointer.code());
        }
    }

    #[test]
    fn текст_ошибки_живёт_до_следующего_вызова() {
        unsafe {
            let mut e: *mut McEngine = std::ptr::null_mut();
            assert_eq!(mc_engine_new(0, &mut e), Status::InvalidArgument.code());
            let mut s = McStr::empty();
            assert_eq!(mc_last_error(&mut s), 0);
            let text = std::str::from_utf8(std::slice::from_raw_parts(s.ptr, s.len)).unwrap();
            assert!(text.contains("шаг проб"), "невнятный текст ошибки: {text}");

            // Удачный вызов стирает прошлую беду: иначе её примут за свежую.
            assert_eq!(mc_engine_new(5000, &mut e), 0);
            let mut s2 = McStr::empty();
            assert_eq!(mc_last_error(&mut s2), 0);
            assert_eq!(s2.len, 0);
            mc_engine_free(e);
        }
    }

    #[test]
    fn история_переживает_перезапуск_и_стареет_честно() {
        unsafe {
            let (e, route) = engine_with_route();
            let mut now = 0u64;
            for i in 0..40 {
                now += 5000;
                probe(e, route, now, 50.0 + (i % 5) as f32);
            }
            let mut buf = McBuffer::empty();
            assert_eq!(mc_engine_save_state(e, now, &mut buf), 0);
            assert!(buf.len > 0);
            mc_engine_free(e);

            // Перезапуск: часы начались заново, каталог объявлен заново.
            let (e2, r2) = engine_with_route();
            assert_eq!(
                mc_engine_load_state(e2, buf.data, buf.len, 60_000, 1_000),
                0
            );
            let mut s = std::mem::zeroed::<McScore>();
            assert_eq!(mc_engine_score(e2, r2, MC_SLA_BROWSE, 60_000, &mut s), 0);
            assert!(s.confidence > 0.0, "история не доехала");

            // А теперь то же состояние, но пролежавшее сутки. Числа те же,
            // свежести нет — и уверенность обязана это показать.
            let (e3, r3) = engine_with_route();
            assert_eq!(
                mc_engine_load_state(e3, buf.data, buf.len, 60_000, 86_400_000),
                0
            );
            let mut s3 = std::mem::zeroed::<McScore>();
            assert_eq!(mc_engine_score(e3, r3, MC_SLA_BROWSE, 60_000, &mut s3), 0);
            assert_eq!(s3.confidence, 0.0, "протухшая история выдана за свежую");

            mc_buffer_free(&mut buf);
            assert!(buf.data.is_null());
            mc_engine_free(e2);
            mc_engine_free(e3);
        }
    }

    #[test]
    fn состояние_чужого_маршрута_на_другой_оси_отбрасывается() {
        unsafe {
            let (e, route) = engine_with_route();
            let mut now = 0u64;
            for _ in 0..40 {
                now += 5000;
                probe(e, route, now, 50.0);
            }
            let mut buf = McBuffer::empty();
            assert_eq!(mc_engine_save_state(e, now, &mut buf), 0);
            mc_engine_free(e);

            // Тот же идентификатор, но маршрут переехал на другую ось: это
            // другой путь под старым именем, и его прошлое ни о чём не говорит.
            let mut e2: *mut McEngine = std::ptr::null_mut();
            assert_eq!(mc_engine_new(5000, &mut e2), 0);
            let mut d = std::mem::zeroed::<McRouteDesc>();
            assert_eq!(mc_route_desc_init(&mut d), 0);
            d.id = McStr::borrow("lt.trojan.8443");
            d.node = McStr::borrow("lt");
            d.transport = McStr::borrow("hysteria2");
            d.country = McStr::borrow("LT");
            d.axis = MC_AXIS_QUIC_UDP;
            let mut r2 = 0u32;
            assert_eq!(mc_engine_add_route(e2, &d, &mut r2), 0);
            assert_eq!(mc_engine_load_state(e2, buf.data, buf.len, now, 0), 0);

            let mut s = std::mem::zeroed::<McScore>();
            assert_eq!(mc_engine_score(e2, r2, MC_SLA_BROWSE, now, &mut s), 0);
            assert_eq!(s.confidence, 0.0, "чужая история пристала к маршруту");

            mc_buffer_free(&mut buf);
            mc_engine_free(e2);
        }
    }

    #[test]
    fn смена_сети_обесценивает_накопленное() {
        unsafe {
            let (e, route) = engine_with_route();
            let mut now = 0u64;
            for _ in 0..40 {
                now += 5000;
                probe(e, route, now, 50.0);
            }
            let mut before = std::mem::zeroed::<McScore>();
            assert_eq!(mc_engine_score(e, route, MC_SLA_BROWSE, now, &mut before), 0);
            assert!(before.confidence > 0.0);

            assert_eq!(mc_engine_network_changed(e), 0);

            let mut after = std::mem::zeroed::<McScore>();
            assert_eq!(mc_engine_score(e, route, MC_SLA_BROWSE, now, &mut after), 0);
            assert_eq!(after.confidence, 0.0, "задержки из другой сети остались в статистике");
            mc_engine_free(e);
        }
    }

    #[test]
    fn закрепление_человеком_сильнее_математики() {
        unsafe {
            let (e, fast) = engine_with_route();
            // Второй маршрут, заведомо худший.
            let mut d = std::mem::zeroed::<McRouteDesc>();
            assert_eq!(mc_route_desc_init(&mut d), 0);
            d.id = McStr::borrow("us.grpc.2083");
            d.node = McStr::borrow("us");
            d.transport = McStr::borrow("vless-grpc");
            d.country = McStr::borrow("US");
            d.axis = MC_AXIS_FAKE_TLS_H2;
            let mut slow = 0u32;
            assert_eq!(mc_engine_add_route(e, &d, &mut slow), 0);

            let mut l = std::mem::zeroed::<McLaneDesc>();
            assert_eq!(mc_lane_desc_init(&mut l), 0);
            l.id = McStr::borrow("web");
            l.title = McStr::borrow("Веб");
            let mut lane = 0u32;
            assert_eq!(mc_engine_add_lane(e, &l, &mut lane), 0);

            let mut now = 0u64;
            for i in 0..90 {
                now += 5000;
                probe(e, fast, now, 40.0 + (i % 5) as f32);
                probe(e, slow, now, 120.0 + (i % 5) as f32);
            }

            assert_eq!(mc_engine_lane_pin(e, lane, slow, now + 600_000), 0);
            let mut dec: *mut McDecision = std::ptr::null_mut();
            assert_eq!(mc_engine_reconcile(e, now, &mut dec), 0);
            let mut n = 0usize;
            assert_eq!(mc_decision_action_count(dec, &mut n), 0);
            assert_eq!(n, 1);
            let mut a = std::mem::zeroed::<McAction>();
            assert_eq!(mc_decision_action(dec, 0, &mut a), 0);
            assert_eq!(a.kind, MC_ACTION_SELECT);
            assert_eq!(a.route, slow, "закрепление проиграло арифметике");
            assert_eq!(a.reason_kind, MC_REASON_USER_PINNED);
            mc_decision_free(dec);
            mc_engine_free(e);
        }
    }
}
