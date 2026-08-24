//! Метка выделения в имени узла: «VIP», «⭐», «[PREMIUM]».
//!
//! Почему метка ищется в ИМЕНИ, а не в отдельном поле подписки: имя записи
//! (фрагмент после `#`) — единственный канал, который провайдер задаёт свободно
//! в любой панели. Заголовка «выдели этот узел» ни одна панель не шлёт, и
//! выдумывать его бессмысленно — его некому выставить.

/// Символьные маркеры. Набор намеренно узкий: чем шире, тем чаще декоративная
/// эмодзи в имени случайно делает узел «золотым». Флаги стран (🇺🇸, 🇫🇮) сюда не
/// входят и не вырезаются никогда.
const SYMBOLS: &[char] = &[
    '\u{2B50}',  // ⭐
    '\u{1F31F}', // 🌟
    '\u{2728}',  // ✨
    '\u{1F451}', // 👑
    '\u{1F48E}', // 💎
    '\u{1F3C6}', // 🏆
    '\u{1F947}', // 🥇
    '\u{2605}',  // ★
    '\u{2606}',  // ☆
    '\u{2726}',  // ✦
    '\u{272A}',  // ✪
    '\u{2730}',  // ✰
];

/// Словесные маркеры. Ищутся по границе слова, без учёта регистра.
const WORDS: &[&str] = &[
    "vip", "premium", "prem", "gold", "pro", "премиум", "вип", "голд",
];

const OPEN: &[char] = &['[', '(', '{', '\u{3010}', '\u{3014}', '<'];
const CLOSE: &[char] = &[']', ')', '}', '\u{3011}', '\u{3015}', '>'];

/// Разобрать имя: очищенное имя и метка выделения (`None` — обычный сервер).
pub fn strip(raw: &str) -> (String, Option<String>) {
    let original = raw.trim();
    let mut symbol_label: Option<String> = None;
    let mut word_label: Option<String> = None;
    let mut name = String::new();

    // 1. Символьные маркеры вырезаем посимвольно.
    for ch in original.chars() {
        if SYMBOLS.contains(&ch) {
            if symbol_label.is_none() {
                symbol_label = Some(ch.to_string());
            }
            continue;
        }
        name.push(ch);
    }

    // 2. Словесные — по границе слова, вместе с обрамляющими скобками.
    while let Some(hit) = find_word(&name) {
        if word_label.is_none() {
            word_label = Some(hit.text.to_uppercase());
        }
        name.replace_range(hit.start..hit.end, "");
    }

    // Когда в имени и звезда, и слово, меткой становится СЛОВО: «VIP» человек
    // читает, «⭐» истолковывает как придётся. Так же поступает эталонная
    // реализация на Swift, и расхождение здесь означало бы разные подписи у
    // одного и того же узла на разных платформах.
    let label = word_label.or(symbol_label);

    // 3. Мусор, оставшийся после вырезания: пустые скобки и висящие разделители.
    let cleaned = tidy(&name);
    // Имя из одних маркеров — не имя. Тогда возвращаем исходную строку целиком:
    // пусть лучше метка не распознается, чем запись останется без названия.
    if cleaned.is_empty() {
        return (original.to_string(), None);
    }
    (cleaned, label)
}

struct Hit {
    start: usize,
    end: usize,
    text: String,
}

/// Найти словесный маркер по границе слова, захватив обрамляющие скобки.
fn find_word(haystack: &str) -> Option<Hit> {
    let lower = haystack.to_lowercase();
    // Нижний регистр может изменить длины (напр. турецкая İ), и тогда индексы
    // разъедутся. Такие имена в подписках не встречаются, но проверить дёшево.
    if lower.len() != haystack.len() {
        return None;
    }

    for word in WORDS {
        let mut from = 0;
        while let Some(rel) = lower[from..].find(word) {
            let start = from + rel;
            let end = start + word.len();
            let before_ok = char_before(&lower, start).map_or(true, |c| !c.is_alphanumeric());
            let after_ok = char_at(&lower, end).map_or(true, |c| !c.is_alphanumeric());
            if before_ok && after_ok {
                // «VIP+» — плюс относится к метке.
                let mut e = end;
                if lower[e..].starts_with('+') {
                    e += 1;
                }
                // Скобки вокруг: `[VIP]`, `【VIP】`.
                let s = expand_open(haystack, start);
                let e2 = expand_close(haystack, e);
                return Some(Hit {
                    start: s,
                    end: e2,
                    text: haystack[start..end].to_string(),
                });
            }
            from = end;
            if from >= lower.len() {
                break;
            }
        }
    }
    None
}

/// Символ перед позицией и символ в позиции. Раздельно, потому что путать их —
/// готовый баг: «vip]» проверялось по символу «p» и слово никогда не находилось.
fn char_before(s: &str, idx: usize) -> Option<char> {
    if idx == 0 {
        return None;
    }
    s[..idx].chars().next_back()
}

fn char_at(s: &str, idx: usize) -> Option<char> {
    if idx >= s.len() {
        return None;
    }
    s[idx..].chars().next()
}

fn expand_open(s: &str, start: usize) -> usize {
    let mut i = start;
    // пробелы
    while i > 0 {
        let prev = s[..i].chars().next_back().unwrap();
        if prev == ' ' {
            i -= prev.len_utf8();
        } else {
            break;
        }
    }
    if i > 0 {
        let prev = s[..i].chars().next_back().unwrap();
        if OPEN.contains(&prev) {
            return i - prev.len_utf8();
        }
    }
    start
}

fn expand_close(s: &str, end: usize) -> usize {
    let mut i = end;
    while i < s.len() {
        let next = s[i..].chars().next().unwrap();
        if next == ' ' {
            i += next.len_utf8();
        } else {
            break;
        }
    }
    if i < s.len() {
        let next = s[i..].chars().next().unwrap();
        if CLOSE.contains(&next) {
            return i + next.len_utf8();
        }
    }
    end
}

/// Убрать пустые скобки и висящие разделители.
fn tidy(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let chars: Vec<char> = s.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        // Пустая пара скобок, возможно с пробелами внутри.
        if OPEN.contains(&chars[i]) {
            let mut j = i + 1;
            while j < chars.len() && chars[j] == ' ' {
                j += 1;
            }
            if j < chars.len() && CLOSE.contains(&chars[j]) {
                i = j + 1;
                continue;
            }
        }
        out.push(chars[i]);
        i += 1;
    }
    // Висящие разделители по краям и сдвоенные пробелы внутри.
    let collapsed = out.split_whitespace().collect::<Vec<_>>().join(" ");
    collapsed
        .trim_matches(|c: char| c == '·' || c == '-' || c == '|' || c == ',' || c.is_whitespace())
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn символьный_маркер_вырезается_и_становится_меткой() {
        let (name, label) = strip("⭐ Method US");
        assert_eq!(name, "Method US");
        assert_eq!(label.as_deref(), Some("⭐"));
    }

    #[test]
    fn флаг_страны_не_маркер() {
        let (name, label) = strip("🇫🇮 NOT_A_RKN-FI · gRPC");
        assert_eq!(name, "🇫🇮 NOT_A_RKN-FI · gRPC");
        assert!(label.is_none(), "флаг вырезан как метка: {label:?}");
    }

    #[test]
    fn словесный_маркер_в_скобках() {
        let (name, label) = strip("[VIP] Токио 01");
        assert_eq!(name, "Токио 01");
        assert_eq!(label.as_deref(), Some("VIP"));
    }

    #[test]
    fn маркер_не_режет_слово_внутри_другого() {
        let (name, label) = strip("Провайдер");
        assert_eq!(name, "Провайдер");
        assert!(label.is_none(), "«про» вырезано из «Провайдер»: {label:?}");
    }

    #[test]
    fn слово_важнее_символа_когда_есть_оба() {
        // Сверено с эталонной реализацией на Swift: «VIP» человек читает,
        // «⭐» истолковывает как придётся.
        let (name, label) = strip("⭐ [VIP] Токио");
        assert_eq!(name, "Токио");
        assert_eq!(label.as_deref(), Some("VIP"));
    }

    #[test]
    fn имя_из_одних_маркеров_остаётся_как_есть() {
        let (name, label) = strip("⭐");
        assert_eq!(name, "⭐");
        assert!(label.is_none());
    }
}
