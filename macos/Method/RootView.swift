import SwiftUI

struct RootView: View {
    /// Свёрнутые блоки. Хранится между запусками: человек, у которого пять
    /// подписок, свернул четыре не для того, чтобы они развернулись обратно.
    @State private var collapsedBlocks: Set<UUID> = RootView.loadCollapsed()
    private static let collapsedKey = "collapsedServerBlocks"

    private static func loadCollapsed() -> Set<UUID> {
        let raw = UserDefaults.standard.stringArray(forKey: collapsedKey) ?? []
        return Set(raw.compactMap(UUID.init(uuidString:)))
    }

    private func toggleCollapsed(_ id: UUID) {
        if collapsedBlocks.contains(id) { collapsedBlocks.remove(id) } else { collapsedBlocks.insert(id) }
        UserDefaults.standard.set(collapsedBlocks.map(\.uuidString), forKey: Self.collapsedKey)
    }

    /// Постоянный ключ блока ручных серверов: подписки у него нет.
    /// Постоянный ключ блока ручных серверов: подписки у него нет, а
    /// свёрнутость хранить надо так же, как у остальных.
    private static let manualBlockID = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!

    @StateObject private var controller = MethodController.shared
    @StateObject private var daemon = MethodDaemon.shared
    @Environment(\.openSettings) private var openSettings
    @State private var tab: Tab = .overview
    @State private var showImport = false

    enum Tab: String, CaseIterable {
        case overview, servers, scenarios
        var title: String {
            switch self {
            case .overview:  return "Обзор"
            case .servers:   return "Серверы"
            case .scenarios: return "Сценарии"
            }
        }
        var icon: String {
            switch self {
            case .overview:  return "house.fill"
            case .servers:   return "server.rack"
            case .scenarios: return "arrow.triangle.branch"
            }
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            titleBar
            HStack(spacing: 0) {
                sidebar
                ZStack {
                    AppBackground(accentGlow: glow, tint: controller.isConnected ? MethodTheme.connected : .white)
                    Group {
                        switch tab {
                        case .overview:  overviewContent
                        case .servers:   serversContent
                        case .scenarios: ScenariosView(controller: controller)
                        }
                    }
                    .id(tab)
                    .transition(.opacity.combined(with: .offset(y: 6)))
                }
            }
        }
        .frame(minWidth: 860, minHeight: 580)
        .background(MethodTheme.background)
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(Color.white.opacity(0.09), lineWidth: 0.5)
        }
        .animation(.spring(response: 0.4, dampingFraction: 0.85), value: tab)
        .onAppear { controller.bootstrap() }
        .sheet(isPresented: $showImport) { ImportView(controller: controller) }
    }

    // MARK: - Заголовок окна

    private var titleBar: some View {
        Text("Method")
            .font(.system(size: 13, weight: .medium))
            .foregroundStyle(MethodTheme.textSecondary)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .frame(height: 40)
            .background(WindowDragArea().background(Color.white.opacity(0.02)))
            .overlay(alignment: .bottom) { Rectangle().fill(MethodTheme.glassStroke).frame(height: 0.5) }
    }

    // MARK: - Сайдбар

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 9) {
                BrandMark()
                Text("Method")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(Color.white.opacity(0.9))
            }
            .padding(.horizontal, 12)
            .padding(.bottom, 18)

            ForEach(Tab.allCases, id: \.self) { item in
                MethodSidebarRow(title: item.title, icon: item.icon, selected: tab == item) { tab = item }
            }

            Button { openSettings() } label: {
                HStack(spacing: 10) {
                    Image(systemName: "gearshape").font(.system(size: 14)).frame(width: 18)
                    Text("Настройки").font(.system(size: 13))
                    Spacer()
                }
                .foregroundStyle(MethodTheme.textSecondary)
                .padding(.horizontal, 12).padding(.vertical, 9)
            }
            .buttonStyle(.plain)

            Spacer()
            statusCard
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 16)
        .frame(width: 190)
        .background(Color.white.opacity(0.015))
        .overlay(alignment: .trailing) { Rectangle().fill(MethodTheme.glassStroke).frame(width: 0.5) }
    }

    private var statusCard: some View {
        VStack(alignment: .leading, spacing: 7) {
            Text("СТАТУС")
                .font(.system(size: 9, weight: .medium)).kerning(0.8)
                .foregroundStyle(MethodTheme.textMuted)
            HStack(spacing: 7) {
                MethodStatusDot(color: statusColor, pulsing: controller.state == .connecting || controller.isFailingOver)
                Text(statusLabel)
                    .font(.system(size: 12))
                    .foregroundStyle(Color.white.opacity(0.62))
                    .monospacedDigit()
            }
        }
        .padding(13)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(MethodTheme.surface)
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(MethodTheme.glassStroke, lineWidth: 0.5))
        )
    }

    // MARK: - Обзор

    private var overviewContent: some View {
        VStack(spacing: 0) {
            Spacer()

            // Надписи над кнопкой нет намеренно: имя приложения уже стоит в
            // заголовке окна и в боковой панели, а третий раз он спорил с
            // кольцом за внимание. Отступ сверху держит Spacer выше.
            ConnectButton(
                title: connectTitle,
                isConnected: controller.isConnected,
                isConnecting: controller.state == .connecting,
                action: { controller.toggleConnection() }
            )
            .background(ConnectAura(isConnected: controller.isConnected, isConnecting: controller.state == .connecting))
            .disabled(controller.selected == nil)
            .opacity(controller.selected == nil ? 0.5 : 1)

            // Действующий маршрут важнее выбранного: человек смотрит сюда,
            // чтобы понять, где он сейчас, а не что нажал десять минут назад.
            if let p = controller.orchestrator.activeProfile ?? controller.selected {
                VStack(spacing: 3) {
                    Text(p.name)
                        .font(.system(size: 15, weight: .medium))
                        .foregroundStyle(Color.white.opacity(0.9))
                    Text("\(p.host) · \(p.protocol.displayName)")
                        .font(.system(size: 12)).monospacedDigit()
                        .foregroundStyle(MethodTheme.textSecondary)
                }
                .padding(.top, 24)

                // Что делает автоматика и почему. Молчащая автоматика
                // читается как своеволие: человек видит, что страна сменилась
                // сама, не понимает причины и выключает её вместе с пользой.
                if controller.adoptedWithoutPlan {
                    // Честнее сказать, что выбор пути не работает, чем
                    // показывать зелёный статус и молчать.
                    VStack(spacing: 4) {
                        Text("Туннель поднят прошлым запуском — выбор пути не работает")
                            .font(.system(size: 11))
                            .foregroundStyle(MethodTheme.trafficYellow.opacity(0.9))
                        Text("Переподключитесь, чтобы включить его")
                            .font(.system(size: 10))
                            .foregroundStyle(MethodTheme.textMuted)
                    }
                    .padding(.top, 10)
                }

                if controller.orchestrator.isRunning {
                    VStack(spacing: 4) {
                        HStack(spacing: 6) {
                            Image(systemName: mainLaneBlocked ? "exclamationmark.octagon.fill" : "shuffle")
                                .font(.system(size: 10))
                            Text(axisSummary)
                                .font(.system(size: 11, weight: mainLaneBlocked ? .semibold : .regular))
                        }
                        .foregroundStyle(mainLaneBlocked ? MethodTheme.trafficRed : MethodTheme.textSecondary)

                        if let reason = controller.orchestrator.lastReason {
                            Text(reason)
                                .font(.system(size: 11))
                                .foregroundStyle(MethodTheme.trafficYellow.opacity(0.85))
                                .multilineTextAlignment(.center)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .padding(.top, 10)
                    .transition(.opacity)
                }
            } else {
                Button { showImport = true } label: {
                    Label("Добавить конфигурацию", systemImage: "plus.circle")
                        .font(.system(size: 13))
                        .foregroundStyle(MethodTheme.textSecondary)
                }
                .buttonStyle(.plain)
                .padding(.top, 24)
            }

            HStack(spacing: 12) {
                stat("Время", controller.formattedDuration())
                stat("IP-адрес", controller.isConnected ? (controller.exitIP ?? "…") : "—")
                stat("Протокол", controller.selected?.protocol.displayName ?? "—")
            }
            .frame(maxWidth: 460)
            .padding(.top, 30)

            if controller.isConnected {
                HStack(spacing: 18) {
                    speed("arrow.down", MethodController.formatSpeed(controller.downloadSpeed), MethodTheme.connected)
                    speed("arrow.up", MethodController.formatSpeed(controller.uploadSpeed),
                          Color(red: 0.36, green: 0.66, blue: 1.0))
                }
                .padding(.top, 20)
                .transition(.opacity)
            }

            if controller.isFailingOver {
                Label("Переключение на другой сервер…", systemImage: "arrow.triangle.2.circlepath")
                    .font(.system(size: 12))
                    .foregroundStyle(MethodTheme.trafficYellow.opacity(0.9))
                    .padding(.top, 14)
            } else if let status = controller.connectAttemptStatus {
                Label(status, systemImage: "arrow.triangle.2.circlepath")
                    .font(.system(size: 12))
                    .foregroundStyle(MethodTheme.textSecondary)
                    .padding(.top, 14)
            } else if case .error(let msg) = controller.state {
                Text(msg)
                    .font(.system(size: 12))
                    .foregroundStyle(MethodTheme.trafficYellow.opacity(0.9))
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 360)
                    .padding(.top, 16)
            }

            if daemon.needsApplicationsInstall {
                Text("Установите Method в /Applications и активируйте демон (⌘,)")
                    .font(.system(size: 11))
                    .foregroundStyle(MethodTheme.textSecondary)
                    .multilineTextAlignment(.center)
                    .padding(.top, 18)
            }

            Spacer()
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .animation(.spring(response: 0.45, dampingFraction: 0.82), value: controller.state)
    }

    /// Сколько независимых способов пройти сейчас доступно и какой в деле.
    /// Именно способов, а не серверов: три сервера на одной оси умирают от
    /// одной причины и запасом друг другу не являются.
    /// Заблокирована ли полоса основного трафика прямо сейчас.
    ///
    /// Если да, «Защищено» на экране — ложь: трафик никуда не идёт. Показывать
    /// зелёный статус в этот момент — тот же молчаливый отказ, выданный за
    /// успех, только наоборот.
    private var mainLaneBlocked: Bool {
        controller.orchestrator.laneBindings["L.web"] == LaneConfigBuilder.Tag.block
    }

    private var axisSummary: String {
        if mainLaneBlocked { return "трафик заблокирован: живых путей нет" }
        let delays = controller.orchestrator.routeDelays
        let alive = delays.count
        let current = controller.orchestrator.laneBindings["L.web"]
        let name = LaneConfigBuilder.EvasionAxisKey.allCases
            .first { LaneConfigBuilder.Tag.axis($0) == current }
            .map(humanAxisName) ?? "подбираем"
        guard alive > 0 else { return "измеряем пути обхода…" }
        return "путь: \(name) · живых способов \(alive)"
    }

    private func humanAxisName(_ axis: LaneConfigBuilder.EvasionAxisKey) -> String {
        switch axis {
        case .quicUDP:    return "QUIC"
        case .fakeTLSH2:  return "gRPC в TLS"
        case .fakeTLSTCP: return "TCP в TLS"
        case .realTLS:    return "настоящий TLS"
        case .rawStream:  return "поток без TLS"
        }
    }

    // MARK: - Серверы (сгруппировано по подпискам)

    private var serversContent: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text("Серверы")
                    .font(.system(size: 26, weight: .bold))
                    .foregroundStyle(MethodTheme.textPrimary)
                Spacer()
                if !controller.subscriptions.isEmpty {
                    Button { Task { await controller.refreshAllSubscriptions() } } label: {
                        Label("Обновить всё", systemImage: "arrow.clockwise")
                            .font(.system(size: 12))
                    }
                    .buttonStyle(PressScale())
                    .foregroundStyle(MethodTheme.textSecondary)
                    .padding(.trailing, 8)
                }
                Button { showImport = true } label: {
                    Image(systemName: "plus")
                        .font(.system(size: 12, weight: .bold))
                        .foregroundStyle(Color.white.opacity(0.8))
                        .frame(width: 28, height: 28)
                        .background(Circle().fill(MethodTheme.surface)
                            .overlay(Circle().strokeBorder(MethodTheme.glassStroke, lineWidth: 0.5)))
                }
                .buttonStyle(.plain)
            }
            .padding(.bottom, 20)

            Group {
                if controller.storedProfiles.isEmpty {
                    emptyState.transition(.opacity)
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 12) {
                            ForEach(controller.subscriptions) { sub in
                                subscriptionSection(sub)
                                    .transition(.opacity.combined(with: .move(edge: .top)))
                            }
                            if !controller.manualProfiles.isEmpty {
                                manualSection
                                    .transition(.opacity.combined(with: .move(edge: .top)))
                            }
                        }
                    }
                    .transition(.opacity)
                }
            }
            .animation(.spring(response: 0.4, dampingFraction: 0.85), value: controller.storedProfiles.isEmpty)
            .animation(.spring(response: 0.4, dampingFraction: 0.85), value: controller.subscriptions.count)
        }
        .padding(36)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func subscriptionSection(_ sub: Subscription) -> some View {
        SubscriptionBlock(
            subscription: sub,
            profiles: controller.profiles(for: sub.id),
            isRefreshing: controller.refreshingSubscriptionIDs.contains(sub.id),
            collapsed: collapsedBlocks.contains(sub.id),
            selectedID: controller.selected?.id,
            ping: { controller.ping(for: $0) },
            onToggle: { withAnimation(.spring(response: 0.32, dampingFraction: 0.86)) { toggleCollapsed(sub.id) } },
            onRefresh: { Task { await controller.refreshSubscription(sub.id) } },
            onRemove: { controller.removeSubscription(sub.id) },
            onSelect: { controller.selectProfile($0) },
            onDeleteProfile: { controller.removeProfile($0) }
        )
    }

    private var manualSection: some View {
        ManualBlock(
            profiles: controller.manualProfiles,
            collapsed: collapsedBlocks.contains(Self.manualBlockID),
            selectedID: controller.selected?.id,
            ping: { controller.ping(for: $0) },
            onToggle: { withAnimation(.spring(response: 0.32, dampingFraction: 0.86)) { toggleCollapsed(Self.manualBlockID) } },
            onSelect: { controller.selectProfile($0) },
            onDeleteProfile: { controller.removeProfile($0) }
        )
    }

    private var emptyState: some View {
        VStack(spacing: 14) {
            Image(systemName: "link.badge.plus")
                .font(.system(size: 36, weight: .ultraLight))
                .foregroundStyle(MethodTheme.textMuted)
            Text("Нет конфигураций")
                .font(.system(size: 15, weight: .medium))
                .foregroundStyle(Color.white.opacity(0.8))
            Text("Вставьте ссылку hysteria2://, vless://, trojan:// или ss://\nлибо адрес подписки (https://…)")
                .font(.system(size: 12))
                .foregroundStyle(MethodTheme.textSecondary)
                .multilineTextAlignment(.center)
            Button { showImport = true } label: {
                Text("Добавить")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(MethodTheme.background)
                    .padding(.horizontal, 18).padding(.vertical, 9)
                    .background(Capsule().fill(.white))
            }
            .buttonStyle(PressScale())
            .padding(.top, 4)
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 60)
    }

    // MARK: - Мелочи

    private func stat(_ title: String, _ value: String) -> some View {
        GlassCard(cornerRadius: 16, padding: 14) {
            VStack(spacing: 6) {
                Text(title.uppercased())
                    .font(.system(size: 9, weight: .medium)).kerning(0.8)
                    .foregroundStyle(MethodTheme.textMuted)
                Text(value)
                    .font(.system(size: 18, weight: .light)).monospacedDigit()
                    .foregroundStyle(Color.white.opacity(0.88))
                    .lineLimit(1).minimumScaleFactor(0.7)
            }
            .frame(maxWidth: .infinity)
        }
    }

    private func speed(_ icon: String, _ value: String, _ tint: Color) -> some View {
        HStack(spacing: 6) {
            Image(systemName: icon).font(.system(size: 11, weight: .bold)).foregroundStyle(tint)
            Text(value).font(.system(size: 13, weight: .medium)).monospacedDigit()
                .foregroundStyle(Color.white.opacity(0.9))
        }
    }

    private var glow: Double {
        switch controller.state {
        case .connected: return 0.10
        case .connecting: return 0.05
        default: return 0.02
        }
    }

    private var connectTitle: String {
        switch controller.state {
        case .connected: return "Отключить"
        case .connecting: return "Подключение…"
        default: return "Подключить"
        }
    }
    private var statusLabel: String {
        if controller.isFailingOver { return "Переключение…" }
        switch controller.state {
        case .connected: return "Защищено"
        case .connecting: return "Подключение…"
        case .error: return "Ошибка"
        default: return "Не подключено"
        }
    }
    private var statusColor: Color {
        switch controller.state {
        case .connected: return MethodTheme.connected
        case .connecting: return MethodTheme.trafficYellow
        case .error: return MethodTheme.trafficRed
        default: return MethodTheme.textSecondary
        }
    }
}

// MARK: - Бренд-марка

struct BrandMark: View {
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 7, style: .continuous)
                .fill(LinearGradient(colors: [Color.white.opacity(0.16), Color.white.opacity(0.04)],
                                     startPoint: .topLeading, endPoint: .bottomTrailing))
                .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .strokeBorder(Color.white.opacity(0.12), lineWidth: 0.5))
            Image(systemName: "link")
                .font(.system(size: 12, weight: .bold))
                .foregroundStyle(.white.opacity(0.85))
        }
        .frame(width: 26, height: 26)
    }
}

// MARK: - Строка сайдбара

struct MethodSidebarRow: View {
    let title: String
    let icon: String
    let selected: Bool
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: icon).font(.system(size: 14)).frame(width: 18)
                Text(title).font(.system(size: 13, weight: selected ? .medium : .regular))
                Spacer()
            }
            .foregroundStyle(selected ? MethodTheme.textPrimary : (hovering ? Color.white.opacity(0.7) : MethodTheme.textSecondary))
            .padding(.horizontal, 12).padding(.vertical, 9)
            .background {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(selected ? Color.white.opacity(0.08) : (hovering ? MethodTheme.hover : .clear))
            }
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

// MARK: - Точка статуса

struct MethodStatusDot: View {
    let color: Color
    var pulsing: Bool = false
    @State private var animate = false

    var body: some View {
        Circle()
            .fill(color)
            .frame(width: 7, height: 7)
            .shadow(color: color.opacity(0.7), radius: 4)
            .overlay {
                if pulsing {
                    Circle().strokeBorder(color.opacity(0.5), lineWidth: 1)
                        .scaleEffect(animate ? 2.6 : 1).opacity(animate ? 0 : 0.8)
                        .animation(.easeOut(duration: 1.2).repeatForever(autoreverses: false), value: animate)
                }
            }
            .onAppear { animate = true }
    }
}
