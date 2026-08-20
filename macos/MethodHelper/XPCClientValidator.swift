import Foundation
import Security
import os.log

/// Кто именно имеет право командовать привилегированным демоном Method.
///
/// # Зачем
///
/// Демон работает под root и умеет `startTunnel(config:)` — то есть поднимает
/// sing-box с произвольным конфигом и заворачивает туда весь трафик машины.
/// Пока `shouldAcceptNewConnection` принимал любое подключение, это мог сделать
/// **любой** локальный процесс: повышение привилегий в чистом виде.
///
/// # Почему не так, как в Method VPN
///
/// У Method VPN требование к клиенту лежит отдельным файлом
/// (`/Library/Application Support/MethodVPN/client.requirement`), который пишет
/// root-установщик `install-helper.sh`. Скопировать эту схему сюда нельзя:
/// standalone-Method ставит демон через `SMAppService`, никакого установщика
/// нет, файл никто не создаёт — проверка «как есть» отвергала бы вообще всех и
/// сломала бы приложение целиком.
///
/// # Что сделано вместо
///
/// Требование **выводится из того самого бандла, внутри которого лежит демон**:
/// `Method.app/Contents/MacOS/network.method.client.helper` → `Method.app`.
/// Правило звучит как «принимаю команды только от приложения, внутри которого
/// я лежу», и считается заново на каждое подключение.
///
/// Следствия:
///   * посторонний процесс не проходит — designated requirement ad-hoc-сборки
///     это `cdhash` главного бинаря приложения, подделать его нельзя;
///   * `SMAppService` ничего дополнительно ставить не нужно;
///   * **пересборка приложения не требует переустановки демона**: cdhash
///     меняется у обоих одновременно, а требование берётся с диска в момент
///     подключения, уже новое. Это ровно та грабля из CLAUDE.md §7.1, из-за
///     которой у Method VPN после каждой пересборки надо звать установщик.
///
/// # Граница доверия
///
/// Требование выводится из бандла, а не пиннится root-ом. Значит, тот, кто
/// умеет писать в `/Applications/Method.app`, может подсунуть своё требование.
/// Но он же может подменить и сам бинарь демона, и `Contents/Resources/sing-box`,
/// которые демон запускает от root, — то есть уже имеет root другим путём.
/// Новой дыры это не открывает: закрывается ровно та, что была (посторонний
/// процесс на машине), и дополнительно проверяется целостность подписи бандла,
/// так что подмена одного лишь `sing-box` без перепoдписи тоже не проходит.
///
/// Наивного TOFU («первый подключившийся становится доверенным») здесь нет
/// намеренно: гонка за первое подключение выигрывается тривиально — демон
/// стартует при загрузке, до входа пользователя в систему.
enum XPCClientValidator {
    private static let logger = OSLog(
        subsystem: "network.method.client.helper",
        category: "XPCAuthorization"
    )

    /// Причина отказа. Разные случаи разделены намеренно: молчаливый отказ —
    /// худший вид отказа, по журналу должно быть видно, что именно не сошлось.
    enum Rejection: Equatable, CustomStringConvertible {
        /// Демон лежит не внутри .app — определить владельца невозможно.
        case appBundleNotFound
        /// Подпись бандла приложения не сходится (подменён файл внутри).
        case appBundleInvalid(OSStatus)
        /// Из бандла не удалось получить designated requirement.
        case requirementUnavailable(OSStatus)
        /// Процесс-клиент не опознан (умер, не подписан, чужой namespace).
        case clientUnknown(OSStatus)
        /// Клиент опознан, но это не наше приложение.
        case clientMismatch(OSStatus)

        var description: String {
            switch self {
            case .appBundleNotFound:
                return "демон запущен не из .app — владельца определить нельзя"
            case .appBundleInvalid(let status):
                return "подпись бандла приложения не сходится (OSStatus \(status))"
            case .requirementUnavailable(let status):
                return "у бандла нет designated requirement (OSStatus \(status))"
            case .clientUnknown(let status):
                return "процесс-клиент не опознан (OSStatus \(status))"
            case .clientMismatch(let status):
                return "клиент не является приложением рядом с демоном (OSStatus \(status))"
            }
        }

        /// Человеческий текст для журнала приложения. Демон отвергает
        /// подключение до обмена сообщениями, поэтому передать это клиенту
        /// по XPC нельзя — тот же диагноз приложение считает у себя само
        /// (см. `MethodDaemon.diagnoseAuthorization()`).
        var humanAdvice: String {
            switch self {
            case .appBundleNotFound:
                return "Демон установлен из повреждённого комплекта. Переустановите Method."
            case .appBundleInvalid:
                return "Файлы Method.app изменились после подписи. "
                    + "Скопируйте приложение в /Applications заново и перезапустите демон."
            case .requirementUnavailable:
                return "Приложение не подписано. Пересоберите Method и скопируйте его в /Applications."
            case .clientUnknown, .clientMismatch:
                return "Демон принимает команды только от приложения, рядом с которым установлен. "
                    + "Откройте /Applications/Method.app."
            }
        }
    }

    // MARK: - Точка входа демона

    static func allows(_ connection: NSXPCConnection) -> Bool {
        guard let helperExecutable = Bundle.main.executableURL,
              let appBundle = AppBundleLocator.appBundleURL(from: helperExecutable) else {
            os_log("Отказ XPC: %{public}s", log: logger, type: .error,
                   Rejection.appBundleNotFound.description)
            return false
        }
        guard let rejection = check(
            appBundleURL: appBundle,
            guestAttributes: guestAttributes(for: connection)
        ) else {
            return true
        }
        os_log("Отказ XPC pid=%d uid=%d: %{public}s", log: logger, type: .error,
               connection.processIdentifier, connection.effectiveUserIdentifier,
               rejection.description)
        return false
    }

    // MARK: - Проверка (вынесена, чтобы её мог дёрнуть тест)

    /// Проверяет процесс с номером `processIdentifier` против бандла `appBundleURL`.
    /// Возвращает `nil`, если всё сошлось.
    static func check(appBundleURL: URL, processIdentifier: pid_t) -> Rejection? {
        check(
            appBundleURL: appBundleURL,
            guestAttributes: [
                kSecGuestAttributePid as String: NSNumber(value: processIdentifier),
            ] as CFDictionary
        )
    }

    static func check(appBundleURL: URL, guestAttributes: CFDictionary) -> Rejection? {
        var staticCode: SecStaticCode?
        let created = SecStaticCodeCreateWithPath(appBundleURL as CFURL, SecCSFlags(), &staticCode)
        guard created == errSecSuccess, let staticCode else {
            return .appBundleNotFound
        }

        // Бандл, из которого выводится требование, обязан быть целым.
        // Иначе требование опишет уже подменённое приложение, а заодно
        // незамеченной пройдёт подмена `Contents/Resources/sing-box`, который
        // демон запускает от root. Проверено: замена одного байта в sing-box
        // валит эту проверку.
        let bundleFlags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures
            | kSecCSCheckNestedCode
            | kSecCSStrictValidate)
        let bundleValid = SecStaticCodeCheckValidity(staticCode, bundleFlags, nil)
        guard bundleValid == errSecSuccess else {
            return .appBundleInvalid(bundleValid)
        }

        var requirement: SecRequirement?
        let copied = SecCodeCopyDesignatedRequirement(staticCode, SecCSFlags(), &requirement)
        guard copied == errSecSuccess, let requirement else {
            return .requirementUnavailable(copied)
        }

        var guestCode: SecCode?
        let guest = SecCodeCopyGuestWithAttributes(nil, guestAttributes, SecCSFlags(), &guestCode)
        guard guest == errSecSuccess, let guestCode else {
            return .clientUnknown(guest)
        }

        let matched = SecCodeCheckValidity(
            guestCode,
            SecCSFlags(rawValue: kSecCSStrictValidate),
            requirement
        )
        guard matched == errSecSuccess else {
            return .clientMismatch(matched)
        }
        return nil
    }

    // MARK: - Идентификация клиента

    /// Как опознавать подключившийся процесс.
    ///
    /// Предпочтителен audit token: он однозначен. Один pid — нет: между
    /// проверкой и работой процесс может умереть, а его номер занять другой
    /// (классическая гонка pid reuse у XPC). Публичного доступа к audit token
    /// у `NSXPCConnection` нет, поэтому берём через KVC и **только** если
    /// свойство реально есть — иначе KVC бросит ObjC-исключение, которое из
    /// Swift не поймать. Если не вышло — откатываемся на pid: это не хуже, чем
    /// было (а было — вообще без проверки).
    private static func guestAttributes(for connection: NSXPCConnection) -> CFDictionary {
        if connection.responds(to: NSSelectorFromString("auditToken")),
           let boxed = connection.value(forKey: "auditToken") as? NSValue,
           String(cString: boxed.objCType) == "{?=[8I]}" {
            var token = audit_token_t()
            let size = MemoryLayout<audit_token_t>.size
            withUnsafeMutableBytes(of: &token) { raw in
                if let base = raw.baseAddress, raw.count == size {
                    boxed.getValue(base, size: size)
                }
            }
            // Страховка от «безопасно и нерабоче»: если приватное свойство
            // однажды поменяет смысл, токен разъедется с pid соединения, и мы
            // молча отвергали бы ВСЕХ. Раскладка audit_token_t фиксированная,
            // val[5] — это pid; сверяем и откатываемся, если не сходится.
            if Int32(bitPattern: token.val.5) == connection.processIdentifier {
                let data = withUnsafeBytes(of: token) { Data($0) }
                return [kSecGuestAttributeAudit as String: data] as CFDictionary
            }
            os_log("audit token не сошёлся с pid соединения, откат на pid",
                   log: logger, type: .error)
        }
        os_log("audit token недоступен, проверяем клиента по pid", log: logger, type: .info)
        return [
            kSecGuestAttributePid as String: NSNumber(value: connection.processIdentifier),
        ] as CFDictionary
    }
}
