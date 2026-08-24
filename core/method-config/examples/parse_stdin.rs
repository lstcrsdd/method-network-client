//! Сверочный прогон: читает ссылки со stdin, печатает канонический JSON.
//! Нужен, чтобы сравнить вывод с эталонным парсером на Swift построчно.
use std::io::{BufRead, Write};

fn main() {
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
                let v = serde_json::to_value(&p).unwrap();
                writeln!(out, "{}", serde_json::to_string(&v).unwrap()).unwrap();
            }
            Err(e) => writeln!(out, "ОШИБКА: {e}").unwrap(),
        }
    }
}
