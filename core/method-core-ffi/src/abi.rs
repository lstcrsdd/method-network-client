//! Простые типы границы: `#[repr(C)]`-структуры, строки как указатель+длина и
//! перечни целочисленных констант.
//!
//! Через границу не проходит ни один сложный тип. Ни `String`, ни `Vec`, ни
//! перечисление Rust с данными, ни трейт-объект: их раскладка в памяти не
//! обещана и меняется от версии компилятора. Всё, что видит C, — целые числа,
//! указатели, длины и плоские структуры с `#[repr(C)]`.
//!
//! Значения констант — часть ABI. Менять их нельзя: заголовок и Swift
//! запомнили именно эти числа. Добавлять новые — можно, в конец.

use crate::error::{Fail, Status};

// ───────────────────────────── Строка ─────────────────────────────

/// Строка через границу: указатель на UTF-8 и длина в БАЙТАХ.
///
/// Нулевого байта внутри не предполагается ни с какой стороны. Это не
/// педантизм: человеческие фразы движка русские, в UTF-8 они по два байта на
/// букву, и `strlen` по ним работает, но всякая арифметика «длина = число
/// символов» врёт. Длина задана явно, и вопрос закрыт.
///
/// Пустая строка — это `ptr = NULL, len = 0` либо любой указатель с нулевой
/// длиной. Оба варианта читаются одинаково.
#[repr(C)]
#[derive(Copy, Clone)]
pub struct McStr {
    pub ptr: *const u8,
    pub len: usize,
}

impl McStr {
    /// Заимствованный вид на строку Rust. Время жизни привязано к источнику
    /// вызывающим, а не выведено компилятором, — за этим следит правило
    /// владения из заголовка.
    pub fn borrow(s: &str) -> McStr {
        McStr { ptr: s.as_ptr(), len: s.len() }
    }

    pub fn empty() -> McStr {
        McStr { ptr: std::ptr::null(), len: 0 }
    }

    /// Прочитать как `&str`.
    ///
    /// # Safety
    /// Вызывающий обещает, что `ptr` указывает на `len` читаемых байт.
    pub unsafe fn as_str<'a>(self, field: &str) -> Result<&'a str, Fail> {
        if self.len == 0 {
            return Ok("");
        }
        if self.ptr.is_null() {
            return Err(Fail::new(
                Status::NullPointer,
                format!("поле «{field}»: длина {} при нулевом указателе", self.len),
            ));
        }
        let bytes = std::slice::from_raw_parts(self.ptr, self.len);
        std::str::from_utf8(bytes).map_err(|e| {
            Fail::new(
                Status::InvalidUtf8,
                format!("поле «{field}» не UTF-8: {e}"),
            )
        })
    }

    /// То же, но пустая строка запрещена: у идентификаторов пустое значение
    /// не «умолчание», а ошибка вызывающего.
    ///
    /// # Safety
    /// См. [`McStr::as_str`].
    pub unsafe fn as_nonempty<'a>(self, field: &str) -> Result<&'a str, Fail> {
        let s = self.as_str(field)?;
        if s.is_empty() {
            return Err(Fail::new(
                Status::InvalidArgument,
                format!("поле «{field}» обязательно и не может быть пустым"),
            ));
        }
        Ok(s)
    }
}

/// Буфер, выданный библиотекой наружу. Освобождается только `mc_buffer_free`.
#[repr(C)]
#[derive(Copy, Clone)]
pub struct McBuffer {
    pub data: *mut u8,
    pub len: usize,
}

impl McBuffer {
    pub fn empty() -> McBuffer {
        McBuffer { data: std::ptr::null_mut(), len: 0 }
    }
}

/// Массив через границу: указатель и число ЭЛЕМЕНТОВ.
///
/// # Safety
/// Вызывающий обещает, что по `ptr` лежит `len` элементов типа `T`.
pub unsafe fn slice_from<'a, T>(
    ptr: *const T,
    len: usize,
    field: &str,
) -> Result<&'a [T], Fail> {
    if len == 0 {
        return Ok(&[]);
    }
    if ptr.is_null() {
        return Err(Fail::new(
            Status::NullPointer,
            format!("поле «{field}»: длина {len} при нулевом указателе"),
        ));
    }
    Ok(std::slice::from_raw_parts(ptr, len))
}

// ───────────────────────── Перечни констант ─────────────────────────
//
// Числа осмысленно совпадают с внутренними, где это возможно: биты экспозиции
// равны битам `ExposureSet`, чтобы маска полосы читалась одинаково по обе
// стороны границы.

pub const MC_AXIS_QUIC_UDP: i32 = 0;
pub const MC_AXIS_FAKE_TLS_H2: i32 = 1;
pub const MC_AXIS_FAKE_TLS_TCP: i32 = 2;
pub const MC_AXIS_REAL_TLS: i32 = 3;
pub const MC_AXIS_RAW_STREAM: i32 = 4;
pub const MC_AXIS_NONE: i32 = 5;

pub const MC_EXPOSURE_TUNNELLED: i32 = 1;
pub const MC_EXPOSURE_DIRECT: i32 = 2;
pub const MC_EXPOSURE_BLOCKED: i32 = 4;

pub const MC_HANDSHAKE_CHEAP: i32 = 0;
pub const MC_HANDSHAKE_EXPENSIVE: i32 = 1;

pub const MC_SLA_REALTIME: i32 = 0;
pub const MC_SLA_BROWSE: i32 = 1;
pub const MC_SLA_STREAM: i32 = 2;
pub const MC_SLA_BULK: i32 = 3;
pub const MC_SLA_SENSITIVE: i32 = 4;

pub const MC_ON_EMPTY_BLOCK: i32 = 0;
pub const MC_ON_EMPTY_HOLD_LAST: i32 = 1;
pub const MC_ON_EMPTY_FALLBACK: i32 = 2;

pub const MC_SWITCH_DRAIN: i32 = 0;
pub const MC_SWITCH_CUT: i32 = 1;

pub const MC_OUTCOME_OK: i32 = 0;
pub const MC_OUTCOME_TIMEOUT: i32 = 1;
pub const MC_OUTCOME_HANDSHAKE_FAILED: i32 = 2;
pub const MC_OUTCOME_EXIT_MISMATCH: i32 = 3;
pub const MC_OUTCOME_DNS_TAMPERED: i32 = 4;
pub const MC_OUTCOME_DISCARDED: i32 = 5;

pub const MC_DISCARD_DEFAULT_ROUTE_THROUGH_TUNNEL: i32 = 0;
pub const MC_DISCARD_NETWORK_CHANGED: i32 = 1;
pub const MC_DISCARD_DEVICE_WAS_ASLEEP: i32 = 2;
pub const MC_DISCARD_CAPTIVE_PORTAL: i32 = 3;
pub const MC_DISCARD_FOREIGN_TUNNEL: i32 = 4;

pub const MC_METRIC_RTT: i32 = 0;
pub const MC_METRIC_JITTER: i32 = 1;
pub const MC_METRIC_LOSS: i32 = 2;
pub const MC_METRIC_THROUGHPUT: i32 = 3;
pub const MC_METRIC_STABILITY: i32 = 4;

pub const MC_DISPLAY_MEASURING: i32 = 0;
pub const MC_DISPLAY_BAND: i32 = 1;
pub const MC_DISPLAY_VALUE: i32 = 2;

/// Ворота — БИТОВАЯ МАСКА: их проваливается сразу несколько, и заставлять
/// вызывающего ходить в библиотеку за списком ради трёх флагов — лишний круг.
pub const MC_GATE_LOSS: u32 = 1 << 0;
pub const MC_GATE_RTT_TAIL: u32 = 1 << 1;
pub const MC_GATE_AVAILABILITY: u32 = 1 << 2;
pub const MC_GATE_DNS_TAMPERED: u32 = 1 << 3;
pub const MC_GATE_EXIT_UNVERIFIED: u32 = 1 << 4;
pub const MC_GATE_HANDSHAKE_FAILED: u32 = 1 << 5;
pub const MC_GATE_IPV6_LEAK: u32 = 1 << 6;

pub const MC_ACTION_SELECT: i32 = 0;
pub const MC_ACTION_DRAIN: i32 = 1;
pub const MC_ACTION_GO_EMPTY: i32 = 2;

pub const MC_REASON_INITIAL: i32 = 0;
pub const MC_REASON_BETTER: i32 = 1;
pub const MC_REASON_EMERGENCY_FACT: i32 = 2;
pub const MC_REASON_AXIS_DEAD: i32 = 3;
pub const MC_REASON_SUPPRESSED: i32 = 4;
pub const MC_REASON_USER_PINNED: i32 = 5;
pub const MC_REASON_MODE_CHANGED: i32 = 6;
pub const MC_REASON_NO_CANDIDATE: i32 = 7;
pub const MC_REASON_DAMPER_OVERRIDDEN: i32 = 8;
/// У обрыва потоков (`MC_ACTION_DRAIN`) причины нет: он всегда следствие
/// соседнего действия, и выдумывать ему отдельную фразу значило бы врать.
pub const MC_REASON_NONE: i32 = -1;

// ───────────────────────── Структуры границы ─────────────────────────

/// Описание маршрута при объявлении каталога.
#[repr(C)]
#[derive(Copy, Clone)]
pub struct McRouteDesc {
    /// `lt.trojan.8443` — свой для каждого маршрута, а не для узла.
    pub id: McStr,
    /// `lt`, `us`, `fi`.
    pub node: McStr,
    /// `hysteria2`, `trojan`, `vless-grpc`.
    pub transport: McStr,
    /// Код страны узла: `LT`, `US`, `FI`. Идёт и в требования полосы
    /// (запрещённые страны), и в человеческие фразы решений.
    pub country: McStr,
    /// Узел выхода для `MC_EXPOSURE_TUNNELLED`. Пусто — берётся `node`.
    /// Отдельное поле нужно для цепочек, где выходной узел не тот, к
    /// которому подключаемся.
    pub exposure_node: McStr,
    pub axis: i32,
    pub exposure: i32,
    pub handshake_cost: i32,
    pub carries_tcp: u8,
    pub carries_udp: u8,
    pub carries_v4: u8,
    pub carries_v6: u8,
}

/// Описание полосы.
#[repr(C)]
#[derive(Copy, Clone)]
pub struct McLaneDesc {
    pub id: McStr,
    /// Человеческое имя: попадает в объяснения решений дословно.
    pub title: McStr,
    /// Зачем полосе разрешён прямой выход. Обязательно, если `allow`
    /// содержит `MC_EXPOSURE_DIRECT`, — иначе полоса не создаётся.
    pub justification: McStr,
    /// Целевая полоса для `MC_ON_EMPTY_FALLBACK`.
    pub on_empty_lane: McStr,
    /// Белый список осей. NULL — без ограничения (не то же самое, что пустой
    /// список: пустой запретил бы всё).
    pub axis_in: *const i32,
    pub axis_in_len: usize,
    /// Чёрный список осей.
    pub axis_not_in: *const i32,
    pub axis_not_in_len: usize,
    /// Белый список стран. NULL — без ограничения.
    pub include_country: *const McStr,
    pub include_country_len: usize,
    /// Чёрный список стран.
    pub exclude_country: *const McStr,
    pub exclude_country_len: usize,
    pub cooldown_ms: u64,
    pub cooldown_max_ms: u64,
    pub margin_floor: f32,
    pub sla: i32,
    pub on_empty: i32,
    pub switch_mode: i32,
    /// Маска допустимых экспозиций: `MC_EXPOSURE_*`, сложенные побитово.
    pub allow: u8,
    pub dwell: u8,
    pub min_axes: u8,
    pub require_udp: u8,
    pub require_v6: u8,
}

/// Одна проба.
#[repr(C)]
#[derive(Copy, Clone)]
pub struct McProbe {
    pub route: u32,
    pub outcome: i32,
    /// Только для `MC_OUTCOME_DISCARDED`: почему замер выброшен.
    pub cause: i32,
    /// Только для `MC_OUTCOME_OK`, миллисекунды.
    pub rtt_ms: f32,
    /// Момент по МОНОТОННЫМ часам платформы, миллисекунды.
    pub at_ms: u64,
    /// Только для `MC_OUTCOME_EXIT_MISMATCH`: через какой узел трафик вышел
    /// на самом деле. Пусто — «вышел мимо, а куда, неизвестно».
    pub got_node: McStr,
}

/// Одно действие исполнителю.
#[repr(C)]
#[derive(Copy, Clone)]
pub struct McAction {
    pub kind: i32,
    pub lane: u32,
    /// Только для `MC_ACTION_SELECT`, иначе 0.
    pub route: u32,
    /// Только для `MC_ACTION_GO_EMPTY`, иначе -1.
    pub on_empty: i32,
    /// Цель для `MC_ON_EMPTY_FALLBACK`, иначе 0.
    pub on_empty_lane: u32,
    pub reason_kind: i32,
    /// Фраза для человека. Живёт ровно столько, сколько объект решения.
    pub reason: McStr,
}

/// Одна запись журнала причин.
#[repr(C)]
#[derive(Copy, Clone)]
pub struct McReason {
    pub kind: i32,
    /// 0 означает вердикт про сеть целиком, а не про полосу (например,
    /// «в этой сети не проходит QUIC»).
    pub lane: u32,
    pub text: McStr,
}

/// Оценка маршрута под класс нагрузки.
#[repr(C)]
#[derive(Copy, Clone)]
pub struct McScore {
    /// 0..100. При провале ворот — ровно 0, и это НЕ «плохое качество», а
    /// дисквалификация: смотри `gates`.
    pub value: f32,
    pub band_lo: f32,
    pub band_hi: f32,
    pub confidence: f32,
    /// `MC_METRIC_*`: что именно ограничивает.
    pub limiter: i32,
    /// `MC_DISPLAY_*`: что интерфейсу РАЗРЕШЕНО показать. Ниже 0.25
    /// уверенности числа нет вовсе.
    pub display: i32,
    /// Маска `MC_GATE_*`. Ненулевая — маршрут дисквалифицирован.
    pub gates: u32,
    pub has_band: u8,
}
