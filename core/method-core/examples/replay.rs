//! Прогон движка на записанном логе измерений.
//!
//! ```sh
//! cargo run -p method-core --example replay -- /tmp/calib.jsonl
//! cargo run -p method-core --example replay -- /tmp/calib.jsonl --sla realtime --tick 5s
//! ```
//!
//! Файл читает ИМЕННО пример, а не ядро: ядро не ходит ни в сеть, ни в
//! файловую систему, и это условие проверяемости, а не эстетика.
//!
//! Лог с замерами в репозиторий не кладётся: он растёт и содержит имена
//! узлов. Работать с ним надо во временном каталоге.

use std::process::ExitCode;

use method_core::axis::ExposureSet;
use method_core::ids::LaneId;
use method_core::lane::{Hysteresis, Lane, OnEmpty, RouteRequirements, SwitchMode};
use method_core::replay::{parse_jsonl, replay, ReplayConfig, Tick};
use method_core::score::SlaClass;

fn класс(s: &str) -> Option<SlaClass> {
    match s {
        "realtime" => Some(SlaClass::Realtime),
        "browse" => Some(SlaClass::Browse),
        "stream" => Some(SlaClass::Stream),
        "bulk" => Some(SlaClass::Bulk),
        "sensitive" => Some(SlaClass::Sensitive),
        _ => None,
    }
}

fn имя_класса(c: SlaClass) -> &'static str {
    match c {
        SlaClass::Realtime => "Реальное время",
        SlaClass::Browse => "Веб",
        SlaClass::Stream => "Видео",
        SlaClass::Bulk => "Загрузки",
        SlaClass::Sensitive => "Чувствительное",
    }
}

/// «5s», «250ms», «5000» — миллисекунды.
fn длительность(s: &str) -> Option<u64> {
    if let Some(v) = s.strip_suffix("ms") {
        v.parse().ok()
    } else if let Some(v) = s.strip_suffix('s') {
        v.parse::<u64>().ok().map(|x| x * 1000)
    } else {
        s.parse().ok()
    }
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() || args[0] == "-h" || args[0] == "--help" {
        eprintln!(
            "Использование: replay <файл.jsonl> [--sla realtime|browse|stream|bulk|sensitive] \
             [--tick per-round|<длительность>] [--include-self] [--timeout <длительность>]"
        );
        return ExitCode::from(2);
    }

    let path = args[0].clone();
    let mut sla = SlaClass::Browse;
    let mut cfg = ReplayConfig::default();
    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "--sla" => {
                i += 1;
                match args.get(i).and_then(|s| класс(s)) {
                    Some(c) => sla = c,
                    None => {
                        eprintln!("не знаю такого класса: {:?}", args.get(i));
                        return ExitCode::from(2);
                    }
                }
            }
            "--tick" => {
                i += 1;
                let v = args.get(i).map(String::as_str).unwrap_or("");
                cfg.tick = if v == "per-round" {
                    Tick::PerRound
                } else {
                    match длительность(v) {
                        Some(ms) => Tick::Every(ms),
                        None => {
                            eprintln!("не разобрал шаг решений: {v:?}");
                            return ExitCode::from(2);
                        }
                    }
                };
            }
            "--timeout" => {
                i += 1;
                match args.get(i).map(String::as_str).and_then(длительность) {
                    Some(ms) => cfg.timeout_ms = ms as f32,
                    None => {
                        eprintln!("не разобрал таймаут");
                        return ExitCode::from(2);
                    }
                }
            }
            "--include-self" => cfg.include_self = true,
            other => {
                eprintln!("неизвестный ключ: {other}");
                return ExitCode::from(2);
            }
        }
        i += 1;
    }

    let text = match std::fs::read_to_string(&path) {
        Ok(t) => t,
        Err(e) => {
            eprintln!("не прочитать {path}: {e}");
            return ExitCode::FAILURE;
        }
    };

    let log = parse_jsonl(&text);

    // Одна полоса с умолчаниями политики: сравниваются именно константы
    // гистерезиса, и добавлять к ним ещё и особенности полосы значило бы
    // мерить сумму двух неизвестных.
    let lane = Lane {
        id: LaneId::new("web"),
        title: имя_класса(sla).into(),
        sla,
        allow: ExposureSet::TUNNELLED,
        justification: None,
        need: RouteRequirements::default(),
        min_axes: 2,
        on_empty: OnEmpty::Block,
        switch: SwitchMode::Drain,
        hysteresis: Hysteresis::default(),
    };

    let rep = replay(&log, std::slice::from_ref(&lane), &cfg);
    print!("{}", rep.human_ru());

    if rep.records_used == 0 {
        eprintln!("\nВ логе нет ни одной пригодной записи — сравнивать нечего.");
        return ExitCode::FAILURE;
    }
    ExitCode::SUCCESS
}
