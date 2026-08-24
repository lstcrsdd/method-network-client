//! Граница слоя конфигурации: разбор ссылок и сборка конфига ядра для
//! Swift, Java (через JNI) и C#.
//!
//! **Почему тут JSON, а у движка — плоские структуры.** Движок зовут каждые
//! несколько секунд, и там дорога каждая аллокация; конфиг собирается один раз
//! на подключение. Плоский ABI на каждое поле профиля был бы огромным и ломался
//! бы при добавлении любой настройки протокола — то есть ровно там, где
//! изменения происходят чаще всего. Строка JSON внутрь, строка JSON наружу:
//! граница не меняется, когда меняются поля.
//!
//! Правила владения — те же, что во всей библиотеке: строки внутрь заимствуются
//! у вызывающего, буфер наружу выдаётся во владение и освобождается только
//! `mc_buffer_free`.

use crate::abi::{McBuffer, McStr};
use crate::error::{Fail, Status};
use method_config::policy::LanePolicy;
use method_config::singbox::Options;
use method_config::stored::{StoredOptions, StoredPolicy};
use serde::Serialize;

/// Выдать строку наружу как буфер во владении вызывающего.
fn emit(out: *mut McBuffer, text: String) -> Result<(), Fail> {
    if out.is_null() {
        return Err(Fail::new(Status::NullPointer, "аргумент «out» равен NULL"));
    }
    let boxed = text.into_bytes().into_boxed_slice();
    let len = boxed.len();
    let data = Box::into_raw(boxed) as *mut u8;
    // SAFETY: непустой указатель проверен выше.
    unsafe { out.write(McBuffer { data, len }) };
    Ok(())
}

fn emit_json<T: Serialize>(out: *mut McBuffer, value: &T) -> Result<(), Fail> {
    let text = serde_json::to_string(value)
        .map_err(|e| Fail::new(Status::Internal, format!("не сериализуется: {e}")))?;
    emit(out, text)
}

/// Разобрать одну share-ссылку. Профиль возвращается JSON-ом.
///
/// # Safety
/// `link` указывает на `len` читаемых байт, `out` — на запись `mc_buffer_t`.
#[no_mangle]
pub unsafe extern "C" fn mc_config_parse_link(link: McStr, out: *mut McBuffer) -> i32 {
    crate::ffi::shield_pub(|| {
        let raw = link.as_str("link")?;
        let profile = method_config::parse(raw)
            .map_err(|e| Fail::invalid(format!("{e}")))?;
        emit_json(out, &profile)
    })
}

/// Разобрать пачку строк (тело подписки). Возвращает
/// `{"profiles": [...], "errors": [{"line": N, "message": "..."}]}`.
///
/// Ошибка одной строки НЕ роняет остальные: подписка на два десятка узлов,
/// потерянная из-за одной кривой записи, — худший вид отказа.
///
/// # Safety
/// См. `mc_config_parse_link`.
#[no_mangle]
pub unsafe extern "C" fn mc_config_parse_many(text: McStr, out: *mut McBuffer) -> i32 {
    crate::ffi::shield_pub(|| {
        let raw = text.as_str("text")?;
        let (profiles, errors) = method_config::parse_many(raw);
        #[derive(Serialize)]
        struct LineError {
            line: usize,
            message: String,
        }
        #[derive(Serialize)]
        struct Out {
            profiles: Vec<method_config::ServerProfile>,
            errors: Vec<LineError>,
        }
        emit_json(
            out,
            &Out {
                profiles,
                errors: errors
                    .into_iter()
                    .map(|(line, e)| LineError {
                        line,
                        message: e.to_string(),
                    })
                    .collect(),
            },
        )
    })
}

/// Собрать конфиг ядра в схеме ПОЛОС.
///
/// `profiles_json` — массив профилей, `policy_json` — политика в переносимом
/// формате (пустая строка означает заводскую), `options_json` — настройки.
///
/// Возвращает `{"config": {...}, "lanes": {...}, "axes": {...}, "routes":
/// {...}, "hosts": [...], "defaultLaneTag": "...", "laneTitles": {...}}` —
/// всё, что нужно управляющему слою, чтобы не разбирать конфиг обратно.
///
/// Отказывает, если политику собирать нельзя: полоса остатка с прямым выходом,
/// прямой выход без объяснения, правило в несуществующую полосу, цикл запасных
/// полос, нехватка независимых осей. Отказ здесь дешевле аварии у человека.
///
/// # Safety
/// Все три строки указывают на читаемые байты, `out` — на запись.
#[no_mangle]
pub unsafe extern "C" fn mc_config_build_lanes(
    profiles_json: McStr,
    policy_json: McStr,
    options_json: McStr,
    out: *mut McBuffer,
) -> i32 {
    crate::ffi::shield_pub(|| {
        let profiles: Vec<method_config::ServerProfile> =
            serde_json::from_str(profiles_json.as_str("profiles_json")?)
                .map_err(|e| Fail::invalid(format!("профили не разбираются: {e}")))?;

        let policy_raw = options_or_empty(policy_json, "policy_json")?;
        let policy = if policy_raw.trim().is_empty() {
            LanePolicy::factory()
        } else {
            serde_json::from_str::<StoredPolicy>(policy_raw)
                .map_err(|e| Fail::invalid(format!("политика не разбирается: {e}")))?
                .into_policy()
        };

        let options: Options = serde_json::from_str::<StoredOptions>(
            options_json.as_str("options_json")?,
        )
        .map_err(|e| Fail::invalid(format!("настройки не разбираются: {e}")))?
        .into();

        let plan = method_config::lanes::build(&profiles, Some(&policy), &options)
            .map_err(|e| Fail::invalid(format!("{e}")))?;

        #[derive(Serialize)]
        #[serde(rename_all = "camelCase")]
        struct Out<'a> {
            config: &'a serde_json::Value,
            lanes: &'a std::collections::BTreeMap<String, Vec<String>>,
            lane_titles: &'a std::collections::BTreeMap<String, String>,
            axes: &'a std::collections::BTreeMap<String, Vec<String>>,
            routes: &'a std::collections::BTreeMap<String, method_config::ServerProfile>,
            hosts: &'a [String],
            default_lane_tag: &'a str,
        }
        emit_json(
            out,
            &Out {
                config: &plan.config,
                lanes: &plan.lanes,
                lane_titles: &plan.lane_titles,
                axes: &plan.axes,
                routes: &plan.routes,
                hosts: &plan.hosts,
                default_lane_tag: &plan.default_lane_tag,
            },
        )
    })
}

/// Пустая строка допустима и означает «по умолчанию»; NULL при нулевой длине —
/// тоже пусто, а не ошибка.
fn options_or_empty<'a>(s: McStr, field: &str) -> Result<&'a str, Fail> {
    if s.ptr.is_null() && s.len == 0 {
        return Ok("");
    }
    // SAFETY: договор границы — указатель на `len` читаемых байт.
    unsafe { s.as_str(field) }
}
