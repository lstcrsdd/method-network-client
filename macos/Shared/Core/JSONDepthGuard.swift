import Foundation

/// Отсекает JSON, вложенный слишком глубоко, ДО передачи его в `JSONSerialization`.
///
/// Зачем это вообще нужно. `JSONSerialization` разбирает документ рекурсией:
/// один уровень вложенности — один кадр стека. Порог глубины у неё свой (около
/// 512), но до этого порога она успевает израсходовать стек. На главном потоке
/// (8 МБ) это незаметно, а вот в задаче Swift Concurrency, возобновлённой на
/// потоке кооперативного пула, и в обработчике XPC стек — сотни килобайт.
/// Там разбор вложенного документа не возвращает ошибку, а убивает процесс:
///
///   EXC_BAD_ACCESS (SIGBUS), «Could not determine thread index for stack guard
///   region», стек из повторяющихся newJSONValue → newJSONObject.
///
/// Воспроизведено на подписке, отдающей тысячу вложенных объектов
/// (`Scripts/subscription_fuzz_test.sh`, случай «вложенный JSON вместо ссылок»):
/// приложение падало ЦЕЛИКОМ, без единого сообщения. И падало бы при каждом
/// автообновлении подписки, то есть при каждом запуске.
///
/// Проверка нерекурсивная — иначе лекарство повторяло бы болезнь.
enum JSONDepthGuard {
    /// Разумный потолок для наших данных. Конфиг sing-box вкладывается на
    /// шесть-семь уровней, ответ панели подписки — на два-три.
    static let maxDepth = 64

    /// Глубина вложенности JSON, посчитанная по тексту за один проход.
    ///
    /// Скобки внутри строковых литералов не считаются: пароль вида `{{{{` —
    /// совершенно законное значение, и принимать его за вложенность значило бы
    /// отвергать годные конфиги.
    static func depth(ofUTF8 bytes: some Sequence<UInt8>) -> Int {
        var current = 0
        var maximum = 0
        var inString = false
        var escaped = false
        for byte in bytes {
            if escaped { escaped = false; continue }
            if inString {
                switch byte {
                case 0x5C: escaped = true       // обратная косая
                case 0x22: inString = false     // кавычка
                default: break
                }
                continue
            }
            switch byte {
            case 0x22: inString = true
            case 0x7B, 0x5B:                    // { [
                current += 1
                if current > maximum { maximum = current }
            case 0x7D, 0x5D:                    // } ]
                current -= 1
            default: break
            }
        }
        return maximum
    }

    /// `true` — документ достаточно плоский, чтобы отдать его `JSONSerialization`.
    static func isSafe(_ data: Data) -> Bool { depth(ofUTF8: data) <= maxDepth }

    /// То же для строки.
    static func isSafe(_ text: String) -> Bool { depth(ofUTF8: text.utf8) <= maxDepth }
}
