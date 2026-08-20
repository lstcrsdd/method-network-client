import SwiftUI

/// Блоки экрана «Серверы»: каждая подписка и группа ручных серверов —
/// отдельная карточка со своим заголовком, состоянием и содержимым.
///
/// Плоский список здесь плохо работает по существу, а не по вкусу: у
/// подписки есть собственное состояние (когда обновлялась, сколько трафика
/// осталось, когда истекает, что не разобралось), и в плоском списке ему
/// негде жить, кроме мелкой подписи, которую никто не читает. Когда подписок
/// две-три, человек должен понимать, к какой относится сервер, не считая
/// строки глазами.

// MARK: - Ось обхода

/// Ось обхода — независимый СПОСОБ пройти, а не протокол.
///
/// Показывается человеку потому, что это единственная величина, по которой
/// он может осмысленно выбрать запасной вариант: два сервера на одной оси
/// умирают от одной и той же причины, и переключение между ними при
/// блокировке не даёт ничего.
enum EvasionAxis: String {
    case quic = "QUIC"
    case fakeTlsH2 = "gRPC в TLS"
    case fakeTlsTcp = "TCP в TLS"
    case realTls = "настоящий TLS"
    case rawStream = "поток без TLS"

    static func of(_ profile: ServerProfile) -> EvasionAxis {
        switch profile.parameters {
        case .hysteria2:
            return .quic
        case .vlessReality(let v):
            // gRPC и Vision — разные оси: их ломают разные вещи, хотя
            // протокол один и тот же.
            return (v.grpcServiceName?.isEmpty == false) ? .fakeTlsH2 : .fakeTlsTcp
        case .trojan:
            return .realTls
        case .shadowsocks:
            return .rawStream
        }
    }
}

/// Значок протокола. Четыре протокола — четыре разных знака: до этого
/// Hysteria2 отличалась от «всего остального», и добавленные Trojan с
/// Shadowsocks были неотличимы от Reality.
struct ProtocolGlyph: View {
    let profile: ServerProfile
    let active: Bool

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: 13.5))
            .foregroundStyle(active ? MethodTheme.connected : Color.white.opacity(0.45))
            .frame(width: 20)
    }

    private var symbol: String {
        switch profile.protocol {
        case .hysteria2:    return "bolt.horizontal.fill"
        case .vlessReality: return "lock.shield.fill"
        case .trojan:       return "checkmark.seal.fill"
        case .shadowsocks:  return "waveform"
        }
    }
}

// MARK: - Выделение, назначенное провайдером

/// Оформление сервера, помеченного провайдером как премиальный/рекомендуемый.
///
/// Золото здесь — только контур и значок. Заливать строку нельзя: по правилу
/// проекта акцент подаётся сдержанно, а сплошная золотая плашка в тёмной теме
/// читается как неон и роняет контраст подписей рядом.
enum FeaturedStyle {
    /// Приглушённое золото, а не жёлтый: рядом уже есть жёлтый предупреждающий
    /// (`trafficYellow`), и путать их нельзя.
    static let gold = Color(red: 0.83, green: 0.68, blue: 0.36)

    static func border(selected: Bool) -> Color { gold.opacity(selected ? 0.55 : 0.30) }
}

/// Значок выделения. Словесную метку («VIP», «ПРЕМИУМ») показываем текстом —
/// она несёт смысл, который значком не передать; чисто символьную —
/// одной звездой.
struct FeaturedBadge: View {
    let label: String

    private var isWord: Bool { label.contains(where: { $0.isLetter }) }

    var body: some View {
        if isWord {
            Text(label)
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(FeaturedStyle.gold)
                .padding(.horizontal, 5)
                .padding(.vertical, 1.5)
                .background(Capsule().fill(FeaturedStyle.gold.opacity(0.12)))
                .overlay(Capsule().strokeBorder(FeaturedStyle.gold.opacity(0.35), lineWidth: 0.5))
                .help("Выделен провайдером")
        } else {
            Image(systemName: "star.fill")
                .font(.system(size: 10))
                .foregroundStyle(FeaturedStyle.gold)
                .help("Выделен провайдером")
        }
    }
}

extension Array where Element == StoredProfile {
    /// Выделенные — выше остальных, внутри групп порядок исходный.
    /// Сортировка по индексу нужна потому, что `sort` в Swift не стабильна:
    /// без него равные элементы каждый раз перетасовываются.
    var featuredFirst: [StoredProfile] {
        enumerated()
            .sorted { a, b in
                let fa = a.element.profile.isFeatured, fb = b.element.profile.isFeatured
                if fa != fb { return fa }
                return a.offset < b.offset
            }
            .map(\.element)
    }
}

// MARK: - Строка сервера

struct ProfileRow: View {
    let profile: ServerProfile
    let selected: Bool
    let ping: Int?
    let onSelect: () -> Void
    let onDelete: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: onSelect) {
            HStack(spacing: 11) {
                ProtocolGlyph(profile: profile, active: selected)

                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Text(profile.name)
                            .font(.system(size: 13, weight: .medium))
                            .foregroundStyle(Color.white.opacity(0.92))
                            .lineLimit(1)
                        if let label = profile.featuredLabel {
                            FeaturedBadge(label: label)
                        }
                    }
                    HStack(spacing: 5) {
                        Text(profile.host)
                            .font(.system(size: 11))
                            .foregroundStyle(MethodTheme.textSecondary)
                            .lineLimit(1)
                        Text("·").foregroundStyle(MethodTheme.textMuted)
                        Text(profile.protocol.displayName)
                            .font(.system(size: 11))
                            .foregroundStyle(MethodTheme.textSecondary)
                        axisTag
                    }
                }

                Spacer(minLength: 8)
                if let ping { pingBadge(ping) }

                if hovering {
                    Button(action: onDelete) {
                        Image(systemName: "trash")
                            .font(.system(size: 11))
                            .foregroundStyle(MethodTheme.trafficRed.opacity(0.85))
                    }
                    .buttonStyle(.plain)
                    .help("Удалить сервер")
                } else if selected {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 14))
                        .foregroundStyle(MethodTheme.connected)
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 9)
            .background {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(selected ? Color.white.opacity(0.07) : (hovering ? MethodTheme.hover : .clear))
                    .overlay(
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .strokeBorder(borderColor, lineWidth: profile.isFeatured ? 0.8 : 0.5)
                    )
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(PressScale())
        .onHover { hovering = $0 }
        .contextMenu { Button("Удалить", role: .destructive, action: onDelete) }
    }

    /// Заливка строки не меняется — выделение живёт только в контуре.
    private var borderColor: Color {
        if profile.isFeatured { return FeaturedStyle.border(selected: selected) }
        return selected ? Color.white.opacity(0.10) : .clear
    }

    private var axisTag: some View {
        Text(EvasionAxis.of(profile).rawValue)
            .font(.system(size: 9.5, weight: .medium))
            .foregroundStyle(MethodTheme.textMuted)
            .padding(.horizontal, 5)
            .padding(.vertical, 1.5)
            .background(
                Capsule().fill(Color.white.opacity(0.05))
            )
    }

    private func pingBadge(_ ms: Int) -> some View {
        let color: Color = ms < 80 ? MethodTheme.connected
            : (ms < 160 ? MethodTheme.trafficYellow : MethodTheme.trafficRed)
        return HStack(spacing: 4) {
            Circle().fill(color).frame(width: 5, height: 5)
            Text("\(ms) мс")
                .font(.system(size: 11)).monospacedDigit()
                .foregroundStyle(color.opacity(0.95))
        }
    }
}

// MARK: - Общая оболочка блока

/// Карточка группы. Заголовок, необязательная полоса состояния и содержимое,
/// которое можно свернуть.
struct ServerBlock<Header: View, Content: View>: View {
    let collapsed: Bool
    let onToggle: () -> Void
    @ViewBuilder var header: () -> Header
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Button(action: onToggle) {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(MethodTheme.textSecondary)
                        .rotationEffect(.degrees(collapsed ? 0 : 90))
                        .frame(width: 14, height: 14)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                header()
            }
            .padding(.horizontal, 14)
            .padding(.top, 12)
            .padding(.bottom, collapsed ? 12 : 10)

            if !collapsed {
                Divider().overlay(Color.white.opacity(0.06))
                VStack(spacing: 2) {
                    content()
                }
                .padding(.horizontal, 6)
                .padding(.vertical, 6)
            }
        }
        .background {
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .fill(MethodTheme.surface)
                .overlay(
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .strokeBorder(MethodTheme.glassStroke, lineWidth: 0.6)
                )
        }
    }
}

// MARK: - Блок подписки

struct SubscriptionBlock: View {
    let subscription: Subscription
    let profiles: [StoredProfile]
    let isRefreshing: Bool
    let collapsed: Bool
    let selectedID: UUID?
    let ping: (UUID) -> Int?
    let onToggle: () -> Void
    let onRefresh: () -> Void
    let onRemove: () -> Void
    let onSelect: (UUID) -> Void
    let onDeleteProfile: (UUID) -> Void

    var body: some View {
        ServerBlock(collapsed: collapsed, onToggle: onToggle) {
            VStack(alignment: .leading, spacing: 5) {
                HStack(spacing: 8) {
                    MethodStatusDot(color: statusColor)
                    Text(subscription.title ?? subscription.name)
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(MethodTheme.textPrimary)
                        .lineLimit(1)
                        .help(subscription.url.absoluteString)
                    Text(countText)
                        .font(.system(size: 11))
                        .foregroundStyle(MethodTheme.textMuted)

                    Spacer(minLength: 8)

                    if let web = subscription.webPageURL {
                        Button { NSWorkspace.shared.open(web) } label: {
                            Image(systemName: "globe").font(.system(size: 11.5))
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(MethodTheme.textSecondary)
                        .help("Сайт провайдера")
                    }
                    if let support = subscription.supportURL {
                        Button { NSWorkspace.shared.open(support) } label: {
                            Image(systemName: "lifepreserver").font(.system(size: 11.5))
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(MethodTheme.textSecondary)
                        .help("Поддержка")
                    }
                    Button(action: onRefresh) {
                        Image(systemName: "arrow.clockwise")
                            .font(.system(size: 11.5))
                            .rotationEffect(.degrees(isRefreshing ? 360 : 0))
                            .animation(
                                isRefreshing
                                    ? .linear(duration: 0.9).repeatForever(autoreverses: false)
                                    : .default,
                                value: isRefreshing
                            )
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(MethodTheme.textSecondary)
                    .help("Обновить подписку")

                    Menu {
                        Button("Копировать ссылку") {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(subscription.url.absoluteString, forType: .string)
                        }
                        Divider()
                        Button("Удалить подписку", role: .destructive, action: onRemove)
                    } label: {
                        Image(systemName: "ellipsis").font(.system(size: 12))
                    }
                    .menuStyle(.borderlessButton)
                    .menuIndicator(.hidden)
                    .frame(width: 18)
                    .foregroundStyle(MethodTheme.textSecondary)
                }

                HStack(spacing: 6) {
                    Text(updatedText)
                        .font(.system(size: 11))
                        .foregroundStyle(MethodTheme.textSecondary)
                    if let hours = subscription.updateIntervalHours, hours > 0 {
                        Text("·").foregroundStyle(MethodTheme.textMuted)
                        Text("автообновление \(hours) ч")
                            .font(.system(size: 11))
                            .foregroundStyle(MethodTheme.textSecondary)
                    }
                    if let expires = expiresText {
                        Text("·").foregroundStyle(MethodTheme.textMuted)
                        Text(expires)
                            .font(.system(size: 11))
                            .foregroundStyle(expiringSoon ? MethodTheme.trafficYellow : MethodTheme.textSecondary)
                    }
                }

                // Пропущенные ссылки показываем явно. Молча потерянный сервер
                // во время блокировки — это человек, который ищет обход, не
                // зная, что у него просто не прочиталась целая ось.
                if let announce = subscription.announce, !announce.isEmpty {
                    Text(announce)
                        .font(.system(size: 11))
                        .foregroundStyle(Color.white.opacity(0.55))
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                }

                if subscription.lastSkippedCount > 0 {
                    Label(
                        "не поддерживается: \(subscription.lastSkippedCount)",
                        systemImage: "exclamationmark.triangle"
                    )
                    .font(.system(size: 10.5))
                    .foregroundStyle(MethodTheme.trafficYellow.opacity(0.9))
                }

                if let error = subscription.lastError {
                    Text(error)
                        .font(.system(size: 11))
                        .foregroundStyle(MethodTheme.trafficRed.opacity(0.9))
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                }

                if let used = subscription.trafficUsed,
                   let total = subscription.trafficTotal, total > 0 {
                    quotaBar(used: used, total: total)
                        .padding(.top, 2)
                }
            }
        } content: {
            if profiles.isEmpty {
                Text("В подписке нет поддерживаемых серверов")
                    .font(.system(size: 12))
                    .foregroundStyle(MethodTheme.textSecondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 8)
            } else {
                ForEach(profiles.featuredFirst) { sp in
                    ProfileRow(
                        profile: sp.profile,
                        selected: selectedID == sp.id,
                        ping: ping(sp.id),
                        onSelect: { onSelect(sp.id) },
                        onDelete: { onDeleteProfile(sp.id) }
                    )
                }
            }
        }
    }

    private var statusColor: Color {
        if subscription.lastError != nil { return MethodTheme.trafficRed }
        if subscription.lastSkippedCount > 0 || expiringSoon { return MethodTheme.trafficYellow }
        if subscription.lastUpdatedAt == nil { return MethodTheme.textMuted }
        return MethodTheme.connected
    }

    private var countText: String {
        let n = profiles.count
        let word: String
        switch (n % 10, n % 100) {
        case (1, let h) where h != 11: word = "сервер"
        case (2...4, let h) where !(11...14).contains(h): word = "сервера"
        default: word = "серверов"
        }
        return "\(n) \(word)"
    }

    private var updatedText: String {
        guard let updated = subscription.lastUpdatedAt else { return "Ещё не обновлялась" }
        let f = RelativeDateTimeFormatter()
        f.unitsStyle = .full
        f.locale = Locale(identifier: "ru_RU")
        return "обновлено " + f.localizedString(for: updated, relativeTo: Date())
    }

    private var expiresText: String? {
        guard let expires = subscription.expiresAt else { return nil }
        let f = DateFormatter()
        f.locale = Locale(identifier: "ru_RU")
        f.dateFormat = "d MMMM"
        return expires < Date() ? "истекла" : "действует до \(f.string(from: expires))"
    }

    private var expiringSoon: Bool {
        guard let expires = subscription.expiresAt else { return false }
        return expires.timeIntervalSinceNow < 3 * 24 * 3600
    }

    private func quotaBar(used: Int64, total: Int64) -> some View {
        let fraction = min(1, Double(used) / Double(total))
        let tint: Color = fraction > 0.9 ? MethodTheme.trafficRed
            : (fraction > 0.75 ? MethodTheme.trafficYellow : MethodTheme.connected)
        return VStack(alignment: .leading, spacing: 4) {
            GeometryReader { geo in
                Capsule().fill(Color.white.opacity(0.08))
                    .overlay(alignment: .leading) {
                        Capsule().fill(tint).frame(width: geo.size.width * fraction)
                    }
            }
            .frame(height: 4)
            Text("\(MethodController.formatBytes(Double(used))) из \(MethodController.formatBytes(Double(total)))")
                .font(.system(size: 10.5)).monospacedDigit()
                .foregroundStyle(MethodTheme.textSecondary)
        }
    }
}

// MARK: - Блок ручных серверов

struct ManualBlock: View {
    let profiles: [StoredProfile]
    let collapsed: Bool
    let selectedID: UUID?
    let ping: (UUID) -> Int?
    let onToggle: () -> Void
    let onSelect: (UUID) -> Void
    let onDeleteProfile: (UUID) -> Void

    var body: some View {
        ServerBlock(collapsed: collapsed, onToggle: onToggle) {
            HStack(spacing: 8) {
                MethodStatusDot(color: MethodTheme.textMuted)
                Text("Мои серверы")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(MethodTheme.textPrimary)
                Text("\(profiles.count)")
                    .font(.system(size: 11))
                    .foregroundStyle(MethodTheme.textMuted)
                Spacer()
                Text("добавлены вручную")
                    .font(.system(size: 11))
                    .foregroundStyle(MethodTheme.textSecondary)
            }
        } content: {
            ForEach(profiles.featuredFirst) { sp in
                ProfileRow(
                    profile: sp.profile,
                    selected: selectedID == sp.id,
                    ping: ping(sp.id),
                    onSelect: { onSelect(sp.id) },
                    onDelete: { onDeleteProfile(sp.id) }
                )
            }
        }
    }
}
