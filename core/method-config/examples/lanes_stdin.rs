//! Сверочный прогон полос: все ссылки со stdin — в один план, план — в JSON.
use std::io::{BufRead, Write};

fn main() {
    let stdin = std::io::stdin();
    let mut profiles = Vec::new();
    for line in stdin.lock().lines() {
        let line = line.unwrap_or_default();
        let t = line.trim();
        if t.is_empty() {
            continue;
        }
        if let Ok(p) = method_config::parse(t) {
            profiles.push(p);
        }
    }
    let opts = method_config::singbox::Options::new("СЕКРЕТ-ДЛЯ-СВЕРКИ");
    let policy = method_config::policy::LanePolicy::factory();
    let mut out = std::io::stdout();
    match method_config::lanes::build(&profiles, Some(&policy), &opts) {
        Ok(plan) => writeln!(out, "{}", serde_json::to_string(&plan.config).unwrap()).unwrap(),
        Err(e) => writeln!(out, "ОТКАЗ: {e}").unwrap(),
    }
}
