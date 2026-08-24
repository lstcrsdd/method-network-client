//! Сверочный прогон сборщика: ссылка со stdin → конфиг ядра в каноническом JSON.
use std::io::{BufRead, Write};

fn main() {
    let opts = method_config::singbox::Options::new("СЕКРЕТ-ДЛЯ-СВЕРКИ");
    let stdin = std::io::stdin();
    let mut out = std::io::stdout();
    for line in stdin.lock().lines() {
        let line = line.unwrap_or_default();
        let t = line.trim();
        if t.is_empty() {
            continue;
        }
        match method_config::parse(t) {
            Ok(p) => {
                let cfg = method_config::singbox::build(&p, &opts);
                writeln!(out, "{}", serde_json::to_string(&cfg).unwrap()).unwrap();
            }
            Err(_) => writeln!(out, "ОШИБКА").unwrap(),
        }
    }
}
