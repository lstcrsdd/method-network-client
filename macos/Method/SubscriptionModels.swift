import Foundation

/// Подписка — URL, отдающий список серверов одним файлом (обычно base64 из
/// share-ссылок, формат v2ray/Xray-панелей вроде Marzban/3x-ui). Локально для Method,
/// в общую модель `ServerProfile` не идёт (чтобы не связывать другие платформы).
struct Subscription: Codable, Identifiable, Equatable {
    var id: UUID
    var url: URL
    var name: String
    var addedAt: Date
    var lastUpdatedAt: Date?
    var lastError: String?
    /// Сколько ссылок из последнего успешного обновления не удалось разобрать
    /// (неподдерживаемый транспорт и т.п.) — чтобы не терять их молча.
    var lastSkippedCount: Int = 0

    /// Как подписка называет себя сама (заголовок `profile-title`). Человек
    /// знает её по этому имени, а не по домену.
    var title: String?
    /// Строка от провайдера (заголовок `announce`).
    var announce: String?
    /// Часы между автообновлениями, как их просит сама подписка.
    var updateIntervalHours: Int?
    /// Страница провайдера и канал поддержки — для отдельных кнопок в блоке.
    var webPageURL: URL?
    var supportURL: URL?

    // Заполняется из заголовка `Subscription-Userinfo`, если сервер его присылает.
    var trafficUsed: Int64?
    var trafficTotal: Int64?
    var expiresAt: Date?

    init(id: UUID = UUID(), url: URL, name: String) {
        self.id = id
        self.url = url
        self.name = name
        self.addedAt = Date()
    }
}

/// Профиль сервера + опциональная привязка к подписке (для группировки и
/// автообновления: при рефреше подписки старые профили с этим subscriptionID
/// заменяются новыми).
struct StoredProfile: Codable, Identifiable, Equatable {
    var profile: ServerProfile
    var subscriptionID: UUID?
    var id: UUID { profile.id }
}
