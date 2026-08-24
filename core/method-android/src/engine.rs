//! Движок выбора маршрута для Android.
//!
//! Тот же движок, что решает на macOS: `method-core` через обёртку с
//! дескрипторами из `method-core-ffi`. Второй реализации нет и не будет —
//! именно из-за отдельных реализаций у Windows до сих пор нет двух осей
//! обхода из пяти.
//!
//! Через границу ходит JSON — как и у слоя конфигурации. Дескриптор маршрута
//! это восемь полей, решение — список действий с объяснениями; описывать это
//! плоским ABI значило бы завести тридцать методов JNI, каждый из которых
//! ломается при добавлении поля.
//!
//! Владение: движок живёт в куче, наружу отдаётся `long`. Java обязана позвать
//! `destroy`, иначе течёт — в языке со сборщиком мусора об этом легко забыть,
//! поэтому обёртка в Java сделана `AutoCloseable`.

use jni::objects::{JClass, JString};
use jni::sys::{jbyteArray, jint, jlong, jstring};
use jni::JNIEnv;
use method_core::axis::{Axis, Exposure, ExposureSet};
use method_core::ids::NodeId;
use method_core::lane::{Hysteresis, OnEmpty, RouteRequirements, SwitchMode};
use method_core::route::{Carries, HandshakeCost};
use method_core::score::SlaClass;
use method_core::Instant;
use method_core_ffi::engine::{Engine, LaneInput, RouteInput};
use serde_json::{json, Value};

use crate::{guard, take};

/// Достать движок по дескриптору.
///
/// # Safety
/// `handle` — значение, выданное `create` и ещё не отданное в `destroy`.
unsafe fn engine<'a>(handle: jlong) -> Result<&'a mut Engine, String> {
    if handle == 0 {
        return Err("движок уже освобождён или не создавался".into());
    }
    Ok(&mut *(handle as *mut Engine))
}

fn axis_from(key: &str) -> Result<Axis, String> {
    Ok(match key {
        "quic-udp" => Axis::QuicUdp,
        "fake-tls-h2" => Axis::FakeTlsH2,
        "fake-tls-tcp" => Axis::FakeTlsTcp,
        "real-tls" => Axis::RealTls,
        "raw-stream" => Axis::RawStream,
        other => return Err(format!("неизвестная ось: {other}")),
    })
}

fn sla_from(key: &str) -> Result<SlaClass, String> {
    Ok(match key {
        "browse" => SlaClass::Browse,
        "realtime" => SlaClass::Realtime,
        "bulk" => SlaClass::Bulk,
        "sensitive" => SlaClass::Sensitive,
        other => return Err(format!("неизвестный класс нагрузки: {other}")),
    })
}

#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodEngine_nativeCreate(
    mut env: JNIEnv,
    _class: JClass,
    probe_interval_ms: jlong,
) -> jlong {
    match Engine::new(probe_interval_ms.max(0) as u64) {
        Ok(e) => Box::into_raw(Box::new(e)) as jlong,
        Err(f) => {
            let _ = env.throw_new("java/lang/IllegalArgumentException", &f.message);
            0
        }
    }
}

#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodEngine_nativeDestroy(
    _env: JNIEnv,
    _class: JClass,
    handle: jlong,
) {
    if handle == 0 {
        return;
    }
    // SAFETY: дескриптор выдан create и отдаётся ровно один раз — за этим
    // следит обёртка в Java, обнуляющая поле после вызова.
    unsafe {
        drop(Box::from_raw(handle as *mut Engine));
    }
}

/// Объявить ОСЬ как маршрут движка.
///
/// В движок регистрируются оси, а не отдельные узлы: внутри оси живого держит
/// само ядро группой urltest, а движок решает то, чего ядро не умеет вовсе, —
/// каким СПОСОБОМ проходить.
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodEngine_nativeAddRoute(
    mut env: JNIEnv,
    _class: JClass,
    handle: jlong,
    descriptor_json: JString,
) -> jint {
    let mut result: i64 = -1;
    let out = guard(&mut env, |env| {
        let raw = take(env, &descriptor_json)?;
        let d: Value = serde_json::from_str(&raw).map_err(|e| e.to_string())?;
        let id = d["id"].as_str().ok_or("нет поля id")?.to_string();
        let axis = axis_from(d["axis"].as_str().ok_or("нет поля axis")?)?;
        let node = d["node"].as_str().unwrap_or("—").to_string();
        // SAFETY: дескриптор проверен на ноль внутри engine().
        let e = unsafe { engine(handle)? };
        let h = e
            .add_route(RouteInput {
                id,
                node: node.clone(),
                transport: d["transport"].as_str().unwrap_or("").to_string(),
                country: d["country"].as_str().unwrap_or("").to_string(),
                axis,
                // Только через туннель. Единственное значение, при котором
                // ошибка вызывающего не превращается в открытый трафик.
                exposure: Exposure::Tunnelled {
                    node: NodeId::new(node),
                },
                carries: Carries::default(),
                handshake_cost: if axis == Axis::QuicUdp {
                    HandshakeCost::Cheap
                } else {
                    HandshakeCost::Expensive
                },
            })
            .map_err(|f| f.message.clone())?;
        result = h as i64;
        Ok(String::new())
    });
    let _ = out;
    result as jint
}

#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodEngine_nativeAddLane(
    mut env: JNIEnv,
    _class: JClass,
    handle: jlong,
    descriptor_json: JString,
) -> jint {
    let mut result: i64 = -1;
    let _ = guard(&mut env, |env| {
        let raw = take(env, &descriptor_json)?;
        let d: Value = serde_json::from_str(&raw).map_err(|e| e.to_string())?;
        let id = d["id"].as_str().ok_or("нет поля id")?.to_string();
        let title = d["title"].as_str().unwrap_or(&id).to_string();
        let sla = sla_from(d["sla"].as_str().unwrap_or("browse"))?;
        let min_axes = d["minAxes"].as_u64().unwrap_or(1) as u8;
        // SAFETY: см. выше.
        let e = unsafe { engine(handle)? };
        let h = e
            .add_lane(LaneInput {
                id,
                title,
                sla,
                // Полосе разрешён только туннель. Прямой выход в движок не
                // отдаётся вовсе: у такой полосы один допустимый исход, и
                // участие движка добавило бы решений там, где их не бывает.
                allow: ExposureSet::TUNNELLED,
                justification: None,
                need: RouteRequirements::default(),
                min_axes,
                on_empty: OnEmpty::Block,
                switch: SwitchMode::Drain,
                hysteresis: Hysteresis::default(),
            })
            .map_err(|f| f.message.clone())?;
        result = h as i64;
        Ok(String::new())
    });
    result as jint
}

/// Записать пробу. `outcome`: «ok» с полем rttMs, «timeout» либо «discarded».
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodEngine_nativeObserve(
    mut env: JNIEnv,
    _class: JClass,
    handle: jlong,
    route: jint,
    at_ms: jlong,
    outcome_json: JString,
) {
    let _ = guard(&mut env, |env| {
        let raw = take(env, &outcome_json)?;
        let d: Value = serde_json::from_str(&raw).map_err(|e| e.to_string())?;
        let kind = d["kind"].as_str().unwrap_or("timeout");
        let outcome = match kind {
            "ok" => method_core::metrics::ProbeOutcome::Ok {
                rtt_ms: d["rttMs"].as_f64().unwrap_or(0.0) as f32,
            },
            "timeout" => method_core::metrics::ProbeOutcome::Timeout,
            // Недостоверный замер не говорит о маршруте ничего, и движок знает
            // этот случай отдельно: наказывать за него узел нельзя.
            "discarded" => method_core::metrics::ProbeOutcome::Discarded {
                cause: method_core::metrics::InvalidCause::NetworkChanged,
            },
            other => return Err(format!("неизвестный исход пробы: {other}")),
        };
        // SAFETY: см. выше.
        let e = unsafe { engine(handle)? };
        e.observe(route as u32, Instant(at_ms.max(0) as u64), outcome)
            .map_err(|f| f.message.clone())?;
        Ok(String::new())
    });
}

/// Спросить решение. Наружу — действия и объяснения в JSON.
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodEngine_nativeReconcile(
    mut env: JNIEnv,
    _class: JClass,
    handle: jlong,
    now_ms: jlong,
) -> jstring {
    guard(&mut env, |_env| {
        // SAFETY: см. выше.
        let e = unsafe { engine(handle)? };
        let decision = e.reconcile(Instant(now_ms.max(0) as u64));
        let actions: Vec<Value> = decision
            .actions
            .iter()
            .map(|a| {
                json!({
                    "kind": a.kind,
                    "lane": a.lane,
                    "route": a.route,
                    "onEmpty": a.on_empty,
                    "text": a.text,
                })
            })
            .collect();
        let reasons: Vec<Value> = decision
            .reasons
            .iter()
            .map(|r| json!({ "lane": r.lane, "text": r.text }))
            .collect();
        serde_json::to_string(&json!({ "actions": actions, "reasons": reasons }))
            .map_err(|e| e.to_string())
    })
}

/// Подтвердить, что переключение состоялось.
///
/// Между «движок решил» и «ядро переключило» лежит запрос, который может не
/// дойти. Без подтверждения движок замолчал бы ровно тогда, когда переключение
/// не произошло.
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodEngine_nativeLaneApplied(
    mut env: JNIEnv,
    _class: JClass,
    handle: jlong,
    lane: jint,
    route: jint,
) {
    let _ = guard(&mut env, |_env| {
        // SAFETY: см. выше.
        let e = unsafe { engine(handle)? };
        e.lane_applied(lane as u32, route as u32)
            .map_err(|f| f.message.clone())?;
        Ok(String::new())
    });
}

/// Выгрузить историю измерений.
///
/// Без переноса истории каждое подключение начинается с трёх минут, когда все
/// маршруты одинаково недостоверны и выбор фактически случаен.
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodEngine_nativeSaveState(
    mut env: JNIEnv,
    _class: JClass,
    handle: jlong,
    now_ms: jlong,
) -> jbyteArray {
    // SAFETY: см. выше.
    let e = match unsafe { engine(handle) } {
        Ok(e) => e,
        Err(m) => {
            let _ = env.throw_new("java/lang/IllegalStateException", m);
            return std::ptr::null_mut();
        }
    };
    match method_core_ffi::state::save(e, Instant(now_ms.max(0) as u64)) {
        Ok(bytes) => match env.byte_array_from_slice(&bytes) {
            Ok(a) => a.into_raw(),
            Err(_) => std::ptr::null_mut(),
        },
        Err(f) => {
            let _ = env.throw_new("java/lang/IllegalStateException", &f.message);
            std::ptr::null_mut()
        }
    }
}

#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodEngine_nativeLoadState(
    mut env: JNIEnv,
    _class: JClass,
    handle: jlong,
    data: jni::objects::JByteArray,
    now_ms: jlong,
    elapsed_ms: jlong,
) {
    let bytes = match env.convert_byte_array(&data) {
        Ok(b) => b,
        Err(e) => {
            let _ = env.throw_new("java/lang/IllegalArgumentException", e.to_string());
            return;
        }
    };
    // SAFETY: см. выше.
    let e = match unsafe { engine(handle) } {
        Ok(e) => e,
        Err(m) => {
            let _ = env.throw_new("java/lang/IllegalStateException", m);
            return;
        }
    };
    if let Err(f) = method_core_ffi::state::load(
        e,
        &bytes,
        Instant(now_ms.max(0) as u64),
        elapsed_ms.max(0) as u64,
    ) {
        let _ = env.throw_new("java/lang/IllegalArgumentException", &f.message);
    }
}

/// Отдать строку наружу — общая с мостом конфигурации.
#[allow(dead_code)]
fn unused(_: jstring) {}
