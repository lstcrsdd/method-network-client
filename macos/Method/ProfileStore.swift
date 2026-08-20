import Foundation
import os.log

/// Персистентность для Method: профили (в т.ч. из подписок) и сами подписки.
/// Файлы в `~/Library/Application Support/Method/`, содержимое зашифровано
/// (см. `SecretStore`) — там лежат пароли и ключи всех серверов.
enum ProfileStorage {
    private static let log = OSLog(subsystem: "network.method.client", category: "Storage")

    private static var baseDir: URL {
        let base = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Method", isDirectory: true)
        try? FileManager.default.createDirectory(
            at: base,
            withIntermediateDirectories: true,
            // Каталог только для владельца: содержимое зашифровано, но и
            // список файлов посторонним знать незачем.
            attributes: [.posixPermissions: 0o700]
        )
        return base
    }

    private static var profilesURL: URL { baseDir.appendingPathComponent("profiles.dat") }
    private static var subscriptionsURL: URL { baseDir.appendingPathComponent("subscriptions.dat") }
    /// Старые открытые файлы. Читаем их один раз, чтобы перенести данные,
    /// и сразу удаляем — оставить их означало бы, что шифрование ничего не
    /// изменило.
    private static var legacyProfilesURL: URL { baseDir.appendingPathComponent("profiles.json") }
    private static var legacySubscriptionsURL: URL { baseDir.appendingPathComponent("subscriptions.json") }

    // MARK: - Профили

    static func loadProfiles() -> [StoredProfile] {
        if let data = readSealed(profilesURL) {
            let profiles = decodeProfiles(data)
            // Прочитано прежним ключом — сразу перекладываем на нынешний,
            // иначе связка ключей спросит разрешение и в следующий раз.
            if SecretStore.lastOpenUsedLegacyKey { saveProfiles(profiles) }
            return profiles
        }
        // Перенос со старого открытого формата.
        guard let legacy = try? Data(contentsOf: legacyProfilesURL) else { return [] }
        let profiles = decodeProfiles(legacy)
        if !profiles.isEmpty {
            saveProfiles(profiles)
            shred(legacyProfilesURL)
            os_log("Профили перенесены в зашифрованное хранилище: %d",
                   log: log, type: .default, profiles.count)
        }
        return profiles
    }

    private static func decodeProfiles(_ data: Data) -> [StoredProfile] {
        if let stored = try? JSONDecoder().decode([StoredProfile].self, from: data) { return stored }
        // Совсем старый формат: просто [ServerProfile], без привязки к подпискам.
        if let legacy = try? JSONDecoder().decode([ServerProfile].self, from: data) {
            return legacy.map { StoredProfile(profile: $0, subscriptionID: nil) }
        }
        return []
    }

    static func saveProfiles(_ profiles: [StoredProfile]) {
        guard let data = try? JSONEncoder().encode(profiles) else { return }
        writeSealed(data, to: profilesURL)
    }

    // MARK: - Подписки

    static func loadSubscriptions() -> [Subscription] {
        if let data = readSealed(subscriptionsURL),
           let decoded = try? JSONDecoder().decode([Subscription].self, from: data) {
            if SecretStore.lastOpenUsedLegacyKey { saveSubscriptions(decoded) }
            return decoded
        }
        guard let legacy = try? Data(contentsOf: legacySubscriptionsURL),
              let decoded = try? JSONDecoder().decode([Subscription].self, from: legacy) else { return [] }
        saveSubscriptions(decoded)
        shred(legacySubscriptionsURL)
        os_log("Подписки перенесены в зашифрованное хранилище: %d",
               log: log, type: .default, decoded.count)
        return decoded
    }

    static func saveSubscriptions(_ subs: [Subscription]) {
        guard let data = try? JSONEncoder().encode(subs) else { return }
        writeSealed(data, to: subscriptionsURL)
    }

    // MARK: - Чтение и запись

    private static func readSealed(_ url: URL) -> Data? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        do {
            return try SecretStore.open(data)
        } catch {
            // Ключ пропал (сброс связки, переезд на другую машину). Молча
            // отдать пустой список нельзя: человек решит, что серверы
            // исчезли сами. Файл сохраняем — вдруг ключ вернётся.
            os_log("Не удалось расшифровать %{public}@: %{public}@",
                   log: log, type: .error, url.lastPathComponent,
                   error.localizedDescription)
            return nil
        }
    }

    private static func writeSealed(_ data: Data, to url: URL) {
        do {
            let sealed = try SecretStore.seal(data)
            try sealed.write(to: url, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                                   ofItemAtPath: url.path)
            // Хранилище ключей не место для облака: без этого файл уедет в
            // резервную копию, ради чего всё и затевалось.
            var values = URLResourceValues()
            values.isExcludedFromBackup = true
            var mutable = url
            try? mutable.setResourceValues(values)
        } catch {
            os_log("Не удалось сохранить %{public}@: %{public}@",
                   log: log, type: .error, url.lastPathComponent,
                   error.localizedDescription)
        }
    }

    /// Затираем содержимое перед удалением: на SSD это не гарантия, но
    /// оставлять пароли в свободных блоках без нужды тоже незачем.
    private static func shred(_ url: URL) {
        if let size = try? Data(contentsOf: url).count, size > 0 {
            try? Data(repeating: 0, count: size).write(to: url, options: .atomic)
        }
        try? FileManager.default.removeItem(at: url)
    }
}
