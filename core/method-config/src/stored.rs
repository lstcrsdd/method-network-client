//! Переносимый формат политики — тот же, что уже лежит на дисках клиентов.
//!
//! Отдельно от доменной модели намеренно. У `LanePolicy` перечисления со
//! связанными значениями, а формат хранения обязан меняться медленнее модели:
//! он записан на дисках у людей, и всякая правка здесь — это миграция.
//!
//! Через границу в чужие языки политика тоже ходит этим форматом: JSON внутрь,
//! JSON наружу. Для слоя конфигурации это правильный размен — структуры
//! сложные, вызовов один на подключение, а плоский ABI на каждое поле был бы
//! огромным и ломался бы от каждой новой настройки.

use crate::lanes::EvasionAxis;
use crate::policy::{Flow, Lane, LanePolicy, Match, OnEmpty};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoredLane {
    pub id: String,
    pub title: String,
    #[serde(default)]
    pub allows_direct: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub justification: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub axis_in: Option<Vec<String>>,
    #[serde(default = "one")]
    pub min_axes: usize,
    /// «block» либо «fallback:<полоса>». Строкой, а не объектом: формат уже
    /// записан на дисках, и менять его ради красоты нельзя.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub on_empty: Option<String>,
}

fn one() -> usize {
    1
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StoredFlow {
    /// «domainSuffix» | «domainKeyword» | «processName» | «port» | «private»
    pub kind: String,
    pub values: Vec<String>,
    pub lane: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub note: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoredPolicy {
    #[serde(default = "one")]
    pub version: usize,
    pub lanes: Vec<StoredLane>,
    pub flows: Vec<StoredFlow>,
    pub default_lane: String,
}

impl From<&LanePolicy> for StoredPolicy {
    fn from(p: &LanePolicy) -> Self {
        StoredPolicy {
            version: 1,
            lanes: p
                .lanes
                .iter()
                .map(|l| StoredLane {
                    id: l.id.clone(),
                    title: l.title.clone(),
                    allows_direct: l.allows_direct,
                    justification: l.justification.clone(),
                    axis_in: l
                        .axis_in
                        .as_ref()
                        .map(|v| v.iter().map(|a| a.key().to_string()).collect()),
                    min_axes: l.min_axes,
                    on_empty: match &l.on_empty {
                        OnEmpty::Block => None,
                        OnEmpty::Fallback(to) => Some(format!("fallback:{to}")),
                    },
                })
                .collect(),
            flows: p
                .flows
                .iter()
                .map(|f| {
                    let (kind, values) = match &f.matcher {
                        Match::DomainSuffix(v) => ("domainSuffix", v.clone()),
                        Match::DomainKeyword(v) => ("domainKeyword", v.clone()),
                        Match::ProcessName(v) => ("processName", v.clone()),
                        Match::Port(v) => ("port", v.iter().map(|p| p.to_string()).collect()),
                        Match::IpIsPrivate => ("private", vec![]),
                    };
                    StoredFlow {
                        kind: kind.to_string(),
                        values,
                        lane: f.lane.clone(),
                        note: f.note.clone(),
                    }
                })
                .collect(),
            default_lane: p.default_lane.clone(),
        }
    }
}

impl StoredPolicy {
    /// Восстановить политику. Неизвестные виды правил ПРОПУСКАЮТСЯ, а не
    /// роняют разбор: файл мог быть записан клиентом новее этого, и потерять
    /// из-за одной строки все остальные правила — хуже, чем потерять её одну.
    pub fn into_policy(self) -> LanePolicy {
        let lanes = self
            .lanes
            .into_iter()
            .map(|l| Lane {
                id: l.id,
                title: l.title,
                allows_direct: l.allows_direct,
                justification: l.justification,
                axis_in: l.axis_in.map(|v| {
                    v.iter()
                        .filter_map(|k| EvasionAxis::ALL.iter().find(|a| a.key() == k).copied())
                        .collect()
                }),
                min_axes: l.min_axes,
                on_empty: match l.on_empty.as_deref() {
                    Some(s) => match s.strip_prefix("fallback:") {
                        Some(to) if !to.is_empty() => OnEmpty::Fallback(to.to_string()),
                        _ => OnEmpty::Block,
                    },
                    None => OnEmpty::Block,
                },
            })
            .collect();

        let flows = self
            .flows
            .into_iter()
            .filter_map(|f| {
                let matcher = match f.kind.as_str() {
                    "domainSuffix" => Match::DomainSuffix(f.values),
                    "domainKeyword" => Match::DomainKeyword(f.values),
                    "processName" => Match::ProcessName(f.values),
                    "port" => Match::Port(f.values.iter().filter_map(|v| v.parse().ok()).collect()),
                    "private" => Match::IpIsPrivate,
                    _ => return None,
                };
                Some(Flow {
                    matcher,
                    lane: f.lane,
                    note: f.note,
                })
            })
            .collect();

        LanePolicy {
            lanes,
            flows,
            default_lane: self.default_lane,
        }
    }
}

/// Настройки сборки в переносимом виде.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoredOptions {
    pub clash_secret: String,
    #[serde(default = "default_controller")]
    pub clash_controller: String,
    #[serde(default = "default_log")]
    pub log_level: String,
    #[serde(default = "default_mtu")]
    pub mtu: u32,
    #[serde(default = "default_probe")]
    pub probe_url: String,
    /// Стек TUN. По умолчанию системный — он верен для macOS и Windows;
    /// Android обязан прислать `gvisor` явно (см. `Options::tun_stack`).
    #[serde(default = "default_stack")]
    pub tun_stack: String,
}

fn default_controller() -> String {
    "127.0.0.1:9090".into()
}
fn default_log() -> String {
    "info".into()
}
fn default_mtu() -> u32 {
    1380
}
fn default_probe() -> String {
    "https://www.gstatic.com/generate_204".into()
}

fn default_stack() -> String {
    "system".into()
}

impl From<StoredOptions> for crate::singbox::Options {
    fn from(s: StoredOptions) -> Self {
        crate::singbox::Options {
            clash_controller: s.clash_controller,
            clash_secret: s.clash_secret,
            log_level: s.log_level,
            mtu: s.mtu,
            probe_url: s.probe_url,
            tun_stack: s.tun_stack,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn политика_переживает_запись_и_чтение() {
        let original = LanePolicy::factory();
        let json = serde_json::to_string(&StoredPolicy::from(&original)).unwrap();
        let back = serde_json::from_str::<StoredPolicy>(&json).unwrap().into_policy();
        assert_eq!(back, original);
    }

    #[test]
    fn неизвестное_правило_не_роняет_остальные() {
        // Файл, записанный клиентом новее этого.
        let json = r#"{
            "version": 2,
            "lanes": [{"id":"web","title":"Весь трафик","minAxes":1}],
            "flows": [
                {"kind":"будущее","values":["x"],"lane":"web"},
                {"kind":"domainSuffix","values":["ok.ru"],"lane":"web"}
            ],
            "defaultLane": "web"
        }"#;
        let p = serde_json::from_str::<StoredPolicy>(json).unwrap().into_policy();
        assert_eq!(p.flows.len(), 1, "потеряны правила, которые понятны");
        assert_eq!(p.flows[0].matcher, Match::DomainSuffix(vec!["ok.ru".into()]));
    }

    #[test]
    fn запасная_полоса_переживает_запись() {
        let mut p = LanePolicy::factory();
        p.lanes[0].on_empty = OnEmpty::Fallback("резерв".into());
        let json = serde_json::to_string(&StoredPolicy::from(&p)).unwrap();
        let back = serde_json::from_str::<StoredPolicy>(&json).unwrap().into_policy();
        assert_eq!(back.lanes[0].on_empty, OnEmpty::Fallback("резерв".into()));
    }
}
