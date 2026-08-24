//! Мост между общим слоем на Rust и Android-клиентом на Java.
//!
//! Почему обёртка написана здесь, а не на C. В JNI два места, где ошибка не
//! ловится компилятором и стоит падения всей виртуальной машины:
//!
//! 1. **Строки.** `GetStringUTFChars` обязан быть отпущен, иначе течёт при
//!    каждом вызове; отпустить дважды — порча кучи.
//! 2. **Исключения.** Бросить исключение и продолжить работать с `JNIEnv`
//!    нельзя: следующий же вызов JNI в состоянии «ожидает исключение» роняет
//!    процесс без объяснений.
//!
//! Крейт `jni` закрывает первое временем жизни, второе — тем, что мы всегда
//! возвращаемся сразу после `throw_new`. Наружу отдаётся JSON строкой: тот же
//! договор, что и у C-границы, чтобы у слоя конфигурации не появилось двух
//! разных представлений.

pub mod engine;

use jni::objects::{JClass, JString};
use jni::sys::jstring;
use jni::JNIEnv;
use std::panic::{catch_unwind, AssertUnwindSafe};

/// Класс исключения, которым сообщаем об отказе. `IllegalArgumentException`, а
/// не своё: отказ здесь всегда означает негодный ВХОД — кривая ссылка,
/// неразбираемая политика, полоса остатка с прямым выходом.
const EXCEPTION: &str = "java/lang/IllegalArgumentException";

/// Достать строку из Java. Пустая ссылка — не ошибка, а пустая строка:
/// вызывающему удобнее передать null для «политики по умолчанию».
pub(crate) fn take(env: &mut JNIEnv, s: &JString) -> Result<String, String> {
    if s.is_null() {
        return Ok(String::new());
    }
    env.get_string(s)
        .map(|v| v.into())
        .map_err(|e| format!("строка не читается: {e}"))
}

/// Отдать строку в Java.
pub(crate) fn give(env: &mut JNIEnv, text: String) -> jstring {
    match env.new_string(text) {
        Ok(s) => s.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}

/// Общий каркас: паника не уходит в виртуальную машину, отказ становится
/// исключением, и после броска мы немедленно возвращаемся.
pub(crate) fn guard<F>(env: &mut JNIEnv, work: F) -> jstring
where
    F: FnOnce(&mut JNIEnv) -> Result<String, String>,
{
    let outcome = catch_unwind(AssertUnwindSafe(|| work(env)));
    match outcome {
        Ok(Ok(text)) => give(env, text),
        Ok(Err(message)) => {
            let _ = env.throw_new(EXCEPTION, message);
            std::ptr::null_mut()
        }
        Err(_) => {
            let _ = env.throw_new(
                EXCEPTION,
                "паника внутри библиотеки поймана на границе; наружу она не ушла",
            );
            std::ptr::null_mut()
        }
    }
}

/// Разобрать одну share-ссылку. Наружу — профиль в JSON.
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodCore_parseLink(
    mut env: JNIEnv,
    _class: JClass,
    link: JString,
) -> jstring {
    guard(&mut env, |env| {
        let raw = take(env, &link)?;
        let profile = method_config::parse(&raw).map_err(|e| e.to_string())?;
        serde_json::to_string(&profile).map_err(|e| e.to_string())
    })
}

/// Разобрать тело подписки. Наружу — `{"profiles":[...],"errors":[...]}`.
///
/// Кривая строка НЕ роняет остальные: подписка на два десятка узлов,
/// потерянная из-за одной записи, — худший вид отказа.
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodCore_parseMany(
    mut env: JNIEnv,
    _class: JClass,
    text: JString,
) -> jstring {
    guard(&mut env, |env| {
        let raw = take(env, &text)?;
        let (profiles, errors) = method_config::parse_many(&raw);
        let errors: Vec<serde_json::Value> = errors
            .into_iter()
            .map(|(line, e)| serde_json::json!({ "line": line, "message": e.to_string() }))
            .collect();
        serde_json::to_string(&serde_json::json!({
            "profiles": profiles,
            "errors": errors,
        }))
        .map_err(|e| e.to_string())
    })
}

/// Собрать конфиг ядра в схеме полос.
///
/// Пустая политика означает заводскую. Отказывает, если политику собирать
/// нельзя, — отказ дешевле аварии у человека.
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodCore_buildLanes(
    mut env: JNIEnv,
    _class: JClass,
    profiles_json: JString,
    policy_json: JString,
    options_json: JString,
) -> jstring {
    guard(&mut env, |env| {
        let profiles_raw = take(env, &profiles_json)?;
        let policy_raw = take(env, &policy_json)?;
        let options_raw = take(env, &options_json)?;

        let profiles: Vec<method_config::ServerProfile> = serde_json::from_str(&profiles_raw)
            .map_err(|e| format!("профили не разбираются: {e}"))?;

        let policy = if policy_raw.trim().is_empty() {
            method_config::policy::LanePolicy::factory()
        } else {
            serde_json::from_str::<method_config::stored::StoredPolicy>(&policy_raw)
                .map_err(|e| format!("политика не разбирается: {e}"))?
                .into_policy()
        };

        let options: method_config::singbox::Options =
            serde_json::from_str::<method_config::stored::StoredOptions>(&options_raw)
                .map_err(|e| format!("настройки не разбираются: {e}"))?
                .into();

        let plan = method_config::lanes::build(&profiles, Some(&policy), &options)
            .map_err(|e| e.to_string())?;

        serde_json::to_string(&serde_json::json!({
            "config": plan.config,
            "lanes": plan.lanes,
            "laneTitles": plan.lane_titles,
            "axes": plan.axes,
            "routes": plan.routes,
            "hosts": plan.hosts,
            "defaultLaneTag": plan.default_lane_tag,
        }))
        .map_err(|e| e.to_string())
    })
}

/// Разбор политики: годна ли и какие правила перекрыты.
///
/// Обе проверки живут в общем слое и вызываются отсюда, а не пишутся заново на
/// Java. Перекрытое правило — это то, до которого очередь не дойдёт никогда:
/// ядро берёт первое совпавшее сверху вниз, и человек видит в списке строку,
/// которая не срабатывает. Поймано на живой проверке macOS: старое
/// «2ip.io → весь трафик» перекрыло новое «2ip.io → мимо туннеля», и разницы
/// в поведении не было.
///
/// Наружу: `{"valid":bool,"error":"…","shadowed":[{"index":N,"by":M}]}`.
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodCore_policyReport(
    mut env: JNIEnv,
    _class: JClass,
    policy_json: JString,
    profiles_json: JString,
) -> jstring {
    guard(&mut env, |env| {
        let policy_raw = take(env, &policy_json)?;
        let profiles_raw = take(env, &profiles_json)?;

        let policy = if policy_raw.trim().is_empty() {
            method_config::policy::LanePolicy::factory()
        } else {
            serde_json::from_str::<method_config::stored::StoredPolicy>(&policy_raw)
                .map_err(|e| format!("политика не разбирается: {e}"))?
                .into_policy()
        };

        let profiles: Vec<method_config::ServerProfile> =
            serde_json::from_str(&profiles_raw).unwrap_or_default();
        let axes: std::collections::BTreeSet<_> = profiles
            .iter()
            .map(method_config::lanes::EvasionAxis::of)
            .collect();

        let (valid, error) = match policy.validate(&axes) {
            Ok(()) => (true, String::new()),
            Err(e) => (false, e.to_string()),
        };
        let shadowed: Vec<serde_json::Value> = policy
            .shadowed_flows()
            .into_iter()
            .map(|(index, by)| serde_json::json!({ "index": index, "by": by }))
            .collect();

        serde_json::to_string(&serde_json::json!({
            "valid": valid,
            "error": error,
            "shadowed": shadowed,
        }))
        .map_err(|e| e.to_string())
    })
}

/// Опись узлов для экрана серверов: что у человека есть и чем оно ходит.
///
/// Группировка по ОСЯМ, а не по странам, и это не украшение. Узел сам по себе
/// не говорит ничего: на одном и том же сервере QUIC даёт 144 мс при джиттере
/// 2, а Reality Vision — 420 мс при джиттере 62 (замер по 117 кругам). Выбор
/// «подключиться к США» не значит почти ничего; значит выбор способа пройти.
/// Поэтому и на экране человек видит в первую очередь способы, а узлы — внутри.
///
/// Ось и её человеческое имя берутся ЗДЕСЬ, в общем слое. На Java это
/// пришлось бы вывести заново из протокола и транспорта — и разойтись с
/// остальными платформами на первой же новинке вроде gRPC.
///
/// Наружу: `{"axes":[{"key","human","nodes":[{"name","host","port","protocol"}]}],
///           "total":N}`.
#[no_mangle]
pub extern "system" fn Java_network_method_client_MethodCore_inventory(
    mut env: JNIEnv,
    _class: JClass,
    profiles_json: JString,
) -> jstring {
    guard(&mut env, |env| {
        let raw = take(env, &profiles_json)?;
        let profiles: Vec<method_config::ServerProfile> =
            serde_json::from_str(&raw).map_err(|e| format!("профили не разбираются: {e}"))?;

        let mut axes = Vec::new();
        // Порядок ОСЕЙ фиксированный, а не по числу узлов: список, который
        // переставляется сам собой при каждой правке подписки, невозможно
        // читать. Пустые оси показываем тоже — отсутствие способа пройти это
        // такой же факт, как его наличие.
        for axis in method_config::lanes::EvasionAxis::ALL {
            let nodes: Vec<serde_json::Value> = profiles
                .iter()
                .filter(|p| method_config::lanes::EvasionAxis::of(p) == axis)
                .map(|p| {
                    serde_json::json!({
                        "name": p.name,
                        "host": p.host,
                        "port": p.port,
                        "protocol": protocol_human(p),
                    })
                })
                .collect();
            axes.push(serde_json::json!({
                "key": axis.key(),
                "human": axis.human(),
                "nodes": nodes,
            }));
        }

        serde_json::to_string(&serde_json::json!({
            "axes": axes,
            "total": profiles.len(),
        }))
        .map_err(|e| e.to_string())
    })
}

/// Как называть протокол человеку. Reality с gRPC и Reality с Vision — это
/// один протокол на двух РАЗНЫХ осях, и в списке они обязаны различаться,
/// иначе два соседних узла выглядят одинаково без всякой причины.
fn protocol_human(p: &method_config::ServerProfile) -> String {
    use method_config::profile::{Parameters, VpnProtocol};
    match (&p.proto, &p.parameters) {
        (VpnProtocol::Hysteria2, _) => "Hysteria2".into(),
        (VpnProtocol::Trojan, _) => "Trojan".into(),
        (VpnProtocol::Shadowsocks, _) => "Shadowsocks-2022".into(),
        (VpnProtocol::VlessReality, Parameters::VlessReality { params })
            if params
                .grpc_service_name
                .as_deref()
                .is_some_and(|s| !s.is_empty()) =>
        {
            "VLESS + Reality · gRPC".into()
        }
        (VpnProtocol::VlessReality, _) => "VLESS + Reality · Vision".into(),
    }
}
