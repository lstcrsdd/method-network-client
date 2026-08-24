//! Политика полос: какой трафик каким ПУТЁМ идёт.
//!
//! Правило называет полосу, а не сервер. Какой маршрут сегодня удовлетворяет
//! требованию — решает движок. Поэтому правила не приходится переписывать при
//! смене узлов, а смена маршрута не трогает правила.

use crate::lanes::EvasionAxis;
use std::collections::{BTreeSet, HashMap};

/// Что делает полоса, когда живых маршрутов не осталось.
///
/// Прямого выхода тут нет и быть не может: это и есть тот случай, ради
/// которого всё строилось. Молчаливый отказ, выданный за успех, опаснее
/// честной ошибки.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OnEmpty {
    Block,
    Fallback(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Lane {
    pub id: String,
    pub title: String,
    pub allows_direct: bool,
    /// Зачем полосе разрешён прямой выход. Обязательно, если разрешён:
    /// «56 доменов» человек проматывает, а объяснение заставляет остановиться.
    pub justification: Option<String>,
    /// Разрешённые оси. `None` — без ограничения.
    pub axis_in: Option<Vec<EvasionAxis>>,
    /// Минимум НЕЗАВИСИМЫХ осей. Полоса без запаса на другой оси не имеет
    /// запаса вообще: протоколы на одной оси убивает одна и та же причина.
    pub min_axes: usize,
    pub on_empty: OnEmpty,
}

impl Lane {
    pub fn new(id: impl Into<String>, title: impl Into<String>) -> Self {
        Lane {
            id: id.into(),
            title: title.into(),
            allows_direct: false,
            justification: None,
            axis_in: None,
            min_axes: 1,
            on_empty: OnEmpty::Block,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Match {
    DomainSuffix(Vec<String>),
    DomainKeyword(Vec<String>),
    ProcessName(Vec<String>),
    Port(Vec<u16>),
    IpIsPrivate,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Flow {
    pub matcher: Match,
    pub lane: String,
    pub note: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LanePolicy {
    pub lanes: Vec<Lane>,
    pub flows: Vec<Flow>,
    pub default_lane: String,
}

impl LanePolicy {
    /// Заводская политика: весь трафик в туннель, локальная сеть мимо.
    pub fn factory() -> Self {
        let mut web = Lane::new("web", "Весь трафик");
        web.min_axes = 2;
        let mut lan = Lane::new("lan", "Локальная сеть");
        lan.allows_direct = true;
        lan.justification = Some("Принтеры, NAS и шлюз в домашней сети".into());
        LanePolicy {
            lanes: vec![web, lan],
            flows: vec![Flow {
                matcher: Match::IpIsPrivate,
                lane: "lan".into(),
                note: None,
            }],
            default_lane: "web".into(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ValidationError {
    EmptyLanes,
    DuplicateLane(String),
    DirectWithoutJustification(String),
    DefaultLaneMissing(String),
    DefaultLaneAllowsDirect(String),
    UnknownLane { lane: String, in_rule: String },
    FallbackToDirectLane { from: String, to: String },
    FallbackCycle(String),
    NotEnoughAxes { lane: String, need: usize, have: usize },
}

impl std::fmt::Display for ValidationError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ValidationError::EmptyLanes => write!(f, "не объявлено ни одной полосы"),
            ValidationError::DuplicateLane(l) => write!(f, "полоса «{l}» объявлена дважды"),
            ValidationError::DirectWithoutJustification(l) => write!(
                f,
                "полосе «{l}» разрешён прямой выход, но не сказано зачем"
            ),
            ValidationError::DefaultLaneMissing(l) => {
                write!(f, "полосы остатка «{l}» не существует")
            }
            ValidationError::DefaultLaneAllowsDirect(l) => write!(
                f,
                "полоса остатка «{l}» выпускает трафик открытым — так весь \
                 неописанный трафик пойдёт мимо туннеля"
            ),
            ValidationError::UnknownLane { lane, in_rule } => {
                write!(f, "правило «{in_rule}» ведёт в неизвестную полосу «{lane}»")
            }
            ValidationError::FallbackToDirectLane { from, to } => write!(
                f,
                "запасная полоса «{to}» для «{from}» выпускает трафик открытым"
            ),
            ValidationError::FallbackCycle(l) => {
                write!(f, "цепочка запасных полос зациклена на «{l}»")
            }
            ValidationError::NotEnoughAxes { lane, need, have } => write!(
                f,
                "полосе «{lane}» нужно {need} независимых способов пройти, а есть {have}"
            ),
        }
    }
}

impl std::error::Error for ValidationError {}

impl LanePolicy {
    /// Проверяет политику и ОТКАЗЫВАЕТ, а не предупреждает.
    ///
    /// Строгость здесь не ради строгости. Профиль маршрутизации без явного
    /// правила на остаток однажды превратил «весь трафик в туннель» в «весь
    /// трафик напрямую» сразу у всех, кто его получил, — и клиент при этом
    /// показывал «подключено». Такое должно быть невозможно собрать, а не
    /// замечено при разборе аварии.
    pub fn validate(&self, available_axes: &BTreeSet<EvasionAxis>) -> Result<(), ValidationError> {
        if self.lanes.is_empty() {
            return Err(ValidationError::EmptyLanes);
        }

        let mut seen: BTreeSet<&str> = BTreeSet::new();
        for lane in &self.lanes {
            if !seen.insert(lane.id.as_str()) {
                return Err(ValidationError::DuplicateLane(lane.id.clone()));
            }
            if lane.allows_direct
                && lane
                    .justification
                    .as_deref()
                    .map(|s| s.trim().is_empty())
                    .unwrap_or(true)
            {
                return Err(ValidationError::DirectWithoutJustification(lane.id.clone()));
            }
        }

        let by_id: HashMap<&str, &Lane> = self.lanes.iter().map(|l| (l.id.as_str(), l)).collect();
        let default_lane = by_id
            .get(self.default_lane.as_str())
            .ok_or_else(|| ValidationError::DefaultLaneMissing(self.default_lane.clone()))?;
        if default_lane.allows_direct {
            return Err(ValidationError::DefaultLaneAllowsDirect(
                self.default_lane.clone(),
            ));
        }

        for flow in &self.flows {
            if !by_id.contains_key(flow.lane.as_str()) {
                return Err(ValidationError::UnknownLane {
                    lane: flow.lane.clone(),
                    in_rule: describe(&flow.matcher),
                });
            }
        }

        // Цепочка запасных полос: не ведёт в прямой выход и не зациклена.
        for lane in &self.lanes {
            let mut visited: BTreeSet<&str> = BTreeSet::new();
            visited.insert(lane.id.as_str());
            let mut current = *by_id.get(lane.id.as_str()).unwrap();
            while let OnEmpty::Fallback(next) = &current.on_empty {
                let target = by_id.get(next.as_str()).ok_or_else(|| {
                    ValidationError::UnknownLane {
                        lane: next.clone(),
                        in_rule: format!("запасная полоса для «{}»", current.id),
                    }
                })?;
                if target.allows_direct {
                    return Err(ValidationError::FallbackToDirectLane {
                        from: current.id.clone(),
                        to: next.clone(),
                    });
                }
                if !visited.insert(next.as_str()) {
                    return Err(ValidationError::FallbackCycle(next.clone()));
                }
                current = *target;
            }
        }

        // Осей должно хватать на требование полосы — иначе «запасной путь»
        // существует только на бумаге.
        for lane in self.lanes.iter().filter(|l| !l.allows_direct) {
            let usable = match &lane.axis_in {
                Some(allowed) => allowed
                    .iter()
                    .filter(|a| available_axes.contains(a))
                    .count(),
                None => available_axes.len(),
            };
            if usable < lane.min_axes {
                return Err(ValidationError::NotEnoughAxes {
                    lane: lane.id.clone(),
                    need: lane.min_axes,
                    have: usable,
                });
            }
        }
        Ok(())
    }

    /// Правила, до которых очередь не дойдёт никогда.
    ///
    /// Ядро берёт первое совпавшее правило сверху вниз. Правило, все значения
    /// которого перечислены выше, не сработает — причём молча: человек видит
    /// строку в списке и считает, что она действует. Поймано на живой
    /// проверке: старое «2ip.io → весь трафик» перекрыло новое «2ip.io → мимо
    /// туннеля», и разницы в поведении не было.
    ///
    /// Возвращает пары «перекрытое правило, кто перекрыл».
    pub fn shadowed_flows(&self) -> Vec<(usize, usize)> {
        let mut out = Vec::new();
        for (i, flow) in self.flows.iter().enumerate() {
            let mine = match values(&flow.matcher) {
                Some(v) if !v.is_empty() => v,
                _ => continue,
            };
            for (j, earlier) in self.flows.iter().enumerate().take(i) {
                if kind(&earlier.matcher) != kind(&flow.matcher) {
                    continue;
                }
                let theirs = match values(&earlier.matcher) {
                    Some(v) => v,
                    None => continue,
                };
                if mine.iter().all(|m| theirs.contains(m)) {
                    out.push((i, j));
                    break;
                }
            }
        }
        out
    }
}

fn kind(m: &Match) -> &'static str {
    match m {
        Match::DomainSuffix(_) => "domainSuffix",
        Match::DomainKeyword(_) => "domainKeyword",
        Match::ProcessName(_) => "processName",
        Match::Port(_) => "port",
        Match::IpIsPrivate => "private",
    }
}

fn values(m: &Match) -> Option<BTreeSet<String>> {
    match m {
        Match::DomainSuffix(v) | Match::DomainKeyword(v) | Match::ProcessName(v) => {
            Some(v.iter().map(|s| s.to_lowercase()).collect())
        }
        Match::Port(v) => Some(v.iter().map(|p| p.to_string()).collect()),
        Match::IpIsPrivate => None,
    }
}

pub fn describe(m: &Match) -> String {
    match m {
        Match::DomainSuffix(d) => format!("домены {}", d.iter().take(2).cloned().collect::<Vec<_>>().join(", ")),
        Match::DomainKeyword(d) => format!("подстроки {}", d.iter().take(2).cloned().collect::<Vec<_>>().join(", ")),
        Match::ProcessName(p) => format!("приложения {}", p.iter().take(2).cloned().collect::<Vec<_>>().join(", ")),
        Match::Port(p) => format!(
            "порты {}",
            p.iter().take(3).map(|x| x.to_string()).collect::<Vec<_>>().join(", ")
        ),
        Match::IpIsPrivate => "локальная сеть".into(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn axes(n: usize) -> BTreeSet<EvasionAxis> {
        [
            EvasionAxis::QuicUdp,
            EvasionAxis::FakeTlsH2,
            EvasionAxis::FakeTlsTcp,
            EvasionAxis::RealTls,
            EvasionAxis::RawStream,
        ]
        .into_iter()
        .take(n)
        .collect()
    }

    #[test]
    fn заводская_политика_проходит_проверку() {
        assert!(LanePolicy::factory().validate(&axes(2)).is_ok());
    }

    #[test]
    fn полоса_остатка_не_может_выпускать_открытым() {
        let mut p = LanePolicy::factory();
        p.default_lane = "lan".into();
        assert_eq!(
            p.validate(&axes(2)),
            Err(ValidationError::DefaultLaneAllowsDirect("lan".into()))
        );
    }

    #[test]
    fn прямой_выход_без_объяснения_не_собирается() {
        let mut p = LanePolicy::factory();
        p.lanes[1].justification = Some("   ".into());
        assert_eq!(
            p.validate(&axes(2)),
            Err(ValidationError::DirectWithoutJustification("lan".into()))
        );
    }

    #[test]
    fn правило_в_несуществующую_полосу_не_собирается() {
        let mut p = LanePolicy::factory();
        p.flows.push(Flow {
            matcher: Match::DomainSuffix(vec!["x.ru".into()]),
            lane: "нет-такой".into(),
            note: None,
        });
        assert!(matches!(
            p.validate(&axes(2)),
            Err(ValidationError::UnknownLane { .. })
        ));
    }

    #[test]
    fn запасная_полоса_не_ведёт_в_прямой_выход() {
        let mut p = LanePolicy::factory();
        p.lanes[0].on_empty = OnEmpty::Fallback("lan".into());
        assert_eq!(
            p.validate(&axes(2)),
            Err(ValidationError::FallbackToDirectLane {
                from: "web".into(),
                to: "lan".into()
            })
        );
    }

    #[test]
    fn цикл_запасных_полос_ловится() {
        let mut a = Lane::new("a", "А");
        a.on_empty = OnEmpty::Fallback("b".into());
        let mut b = Lane::new("b", "Б");
        b.on_empty = OnEmpty::Fallback("a".into());
        let p = LanePolicy {
            lanes: vec![a, b],
            flows: vec![],
            default_lane: "a".into(),
        };
        assert!(matches!(
            p.validate(&axes(2)),
            Err(ValidationError::FallbackCycle(_))
        ));
    }

    #[test]
    fn запас_на_другой_оси_обязателен_а_не_желателен() {
        // Полосе нужно два независимых способа, а ось одна.
        assert_eq!(
            LanePolicy::factory().validate(&axes(1)),
            Err(ValidationError::NotEnoughAxes {
                lane: "web".into(),
                need: 2,
                have: 1
            })
        );
    }

    #[test]
    fn перекрытое_правило_находится() {
        let mut p = LanePolicy::factory();
        p.flows.push(Flow {
            matcher: Match::DomainSuffix(vec!["2ip.io".into()]),
            lane: "web".into(),
            note: None,
        });
        p.flows.push(Flow {
            matcher: Match::DomainSuffix(vec!["2ip.io".into()]),
            lane: "lan".into(),
            note: None,
        });
        let shadowed = p.shadowed_flows();
        assert_eq!(shadowed, vec![(2, 1)], "перекрытие не обнаружено");
    }
}
