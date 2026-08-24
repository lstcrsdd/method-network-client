//! Общий слой конфигурации Method: модель профиля, разбор share-ссылок и
//! (следующим заходом) сборка конфига ядра.
//!
//! Зачем крейт вообще появился. Модель, парсер и сборщик были написаны дважды —
//! на Swift для macOS и на C# для Windows, — а под Android понадобилась бы
//! третья копия на Java. Расхождение началось сразу: у Windows нет Trojan и
//! Shadowsocks, то есть две оси обхода из пяти отсутствуют не по замыслу, а
//! потому что код не общий.
//!
//! Крейт сознательно БЕЗ ввода-вывода: ни сети, ни файлов, ни часов. На вход
//! строка, на выход структура. Такое одинаково зовётся из Swift, из JNI и из
//! C# и одинаково проверяется тестами.

pub mod link;
pub mod marker;
pub mod lanes;
pub mod policy;
pub mod profile;
pub mod singbox;
pub mod stored;

pub use link::{parse, parse_many, ParseError};
pub use profile::{Parameters, ServerProfile, VpnProtocol};
