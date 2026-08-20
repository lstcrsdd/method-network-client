import SwiftUI
import ServiceManagement

/// Настоящее системное окно настроек macOS (Settings-сцена, ⌘,), с вкладками —
/// как у System Settings / Safari / Mail. Три вкладки: Общие, Подписки, Демон.
struct MethodSettingsView: View {
    var body: some View {
        TabView {
            GeneralSettingsTab()
                .tabItem { Label("Общие", systemImage: "slider.horizontal.3") }
            SubscriptionsSettingsTab()
                .tabItem { Label("Подписки", systemImage: "arrow.triangle.2.circlepath") }
            DaemonSettingsTab()
                .tabItem { Label("Демон", systemImage: "bolt.shield") }
        }
        .frame(width: 480, height: 380)
        .background(MethodTheme.background)
    }
}

// MARK: - Общие

private struct GeneralSettingsTab: View {
    @StateObject private var controller = MethodController.shared
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled
    @State private var launchAtLoginError: String?

    private static let intervals: [Double] = [0, 1, 3, 6, 12, 24]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                GroupCard {
                    SettingsRow(title: "Блокировать трафик мимо туннеля",
                                subtitle: "Если туннель упадёт, интернет отключится, а не пойдёт открытым") {
                        MethodToggle(isOn: $controller.killSwitchEnabled)
                    }
                    SettingsRow(title: "Автопереподключение",
                                subtitle: "После сна или смены сети") {
                        MethodToggle(isOn: $controller.autoReconnect)
                    }
                    SettingsRow(title: "Запускать при входе в систему", showDivider: false) {
                        MethodToggle(isOn: Binding(
                            get: { launchAtLogin },
                            set: { toggleLaunchAtLogin($0) }
                        ))
                    }
                }
                if let launchAtLoginError {
                    Text(launchAtLoginError)
                        .font(.system(size: 11))
                        .foregroundStyle(MethodTheme.trafficYellow.opacity(0.9))
                }

                SectionLabel(text: "Устройство")
                GroupCard {
                    SettingsRow(title: "HWID",
                                subtitle: DeviceIdentity.shortHWID,
                                showDivider: false) {
                        Button("Скопировать") {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(DeviceIdentity.hwid, forType: .string)
                        }
                        .buttonStyle(PressScale())
                    }
                }
                Text("Этот HWID отправляется в панель при загрузке подписки.")
                    .font(.system(size: 11))
                    .foregroundStyle(MethodTheme.textSecondary)

                SectionLabel(text: "Автообновление подписок")
                GroupCard {
                    ForEach(Array(Self.intervals.enumerated()), id: \.offset) { i, hours in
                        SettingsRow(title: intervalTitle(hours), showDivider: i < Self.intervals.count - 1) {
                            if controller.autoRefreshIntervalHours == hours {
                                Image(systemName: "checkmark").font(.system(size: 12, weight: .bold))
                                    .foregroundStyle(MethodTheme.connected)
                            }
                        }
                        .contentShape(Rectangle())
                        .onTapGesture { controller.autoRefreshIntervalHours = hours }
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(MethodTheme.background)
    }

    private func intervalTitle(_ hours: Double) -> String {
        if hours == 0 { return "Не обновлять автоматически" }
        if hours < 24 { return "Каждые \(Int(hours)) ч" }
        return "Раз в сутки"
    }

    private func toggleLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
            launchAtLogin = enabled
            launchAtLoginError = nil
        } catch {
            launchAtLoginError = error.localizedDescription
        }
    }
}

// MARK: - Подписки

private struct SubscriptionsSettingsTab: View {
    @StateObject private var controller = MethodController.shared
    @State private var newURL = ""
    @State private var isAdding = false
    @State private var addError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 8) {
                TextField("https://example.com/sub/…", text: $newURL)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(size: 12, design: .monospaced))
                    .onSubmit { addSubscription() }
                Button {
                    addSubscription()
                } label: {
                    if isAdding { ProgressView().controlSize(.small) } else { Text("Добавить") }
                }
                .buttonStyle(PressScale())
                .disabled(isAdding || newURL.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            if let addError {
                Text(addError).font(.system(size: 11)).foregroundStyle(MethodTheme.trafficYellow.opacity(0.9))
            }

            if controller.subscriptions.isEmpty {
                Spacer()
                VStack(spacing: 8) {
                    Image(systemName: "arrow.triangle.2.circlepath")
                        .font(.system(size: 28, weight: .ultraLight))
                        .foregroundStyle(MethodTheme.textMuted)
                    Text("Нет подписок").font(.system(size: 13)).foregroundStyle(MethodTheme.textSecondary)
                }
                .frame(maxWidth: .infinity)
                Spacer()
            } else {
                ScrollView {
                    VStack(spacing: 6) {
                        ForEach(controller.subscriptions) { sub in
                            subscriptionRow(sub)
                        }
                    }
                }
            }
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(MethodTheme.background)
    }

    private func subscriptionRow(_ sub: Subscription) -> some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(sub.name).font(.system(size: 13, weight: .medium)).foregroundStyle(Color.white.opacity(0.9))
                Text(sub.url.absoluteString).font(.system(size: 10.5)).foregroundStyle(MethodTheme.textSecondary).lineLimit(1)
            }
            Spacer()
            Button { Task { await controller.refreshSubscription(sub.id) } } label: {
                Image(systemName: "arrow.clockwise").font(.system(size: 11))
            }.buttonStyle(.plain).foregroundStyle(MethodTheme.textSecondary)
            Button { controller.removeSubscription(sub.id) } label: {
                Image(systemName: "trash").font(.system(size: 11))
            }.buttonStyle(.plain).foregroundStyle(MethodTheme.trafficRed.opacity(0.85))
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(MethodTheme.surface))
    }

    private func addSubscription() {
        guard let url = URL(string: newURL.trimmingCharacters(in: .whitespaces)),
              let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https" else {
            addError = "Некорректная ссылка"
            return
        }
        isAdding = true
        addError = nil
        Task {
            do {
                try await controller.addSubscription(url: url)
                await MainActor.run { newURL = ""; isAdding = false }
            } catch {
                await MainActor.run { addError = error.localizedDescription; isAdding = false }
            }
        }
    }
}

// MARK: - Демон

private struct DaemonSettingsTab: View {
    @StateObject private var daemon = MethodDaemon.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            GroupCard {
                SettingsRow(title: "Установить в /Applications",
                            subtitle: daemon.isRunningFromApplications ? "Установлено" : "Требуется для демона") {
                    Button("Копировать") { daemon.copyToApplications() }
                        .buttonStyle(PressScale())
                        .disabled(daemon.isRunningFromApplications)
                }
                SettingsRow(title: "Привилегированный демон",
                            subtitle: statusSubtitle, showDivider: false) {
                    daemonActionButton
                }
            }
            Text("После пересборки в Xcode: сначала «Копировать», затем «Перезапустить» —"
                + " демон не подхватывает новый код сам, он держит уже загруженный процесс.")
                .font(.system(size: 11))
                .foregroundStyle(MethodTheme.textMuted)
            if daemon.status == .requiresApproval {
                Text("macOS требует подтвердить демон вручную: Настройки → Основные → Элементы входа и расширения.")
                    .font(.system(size: 11))
                    .foregroundStyle(MethodTheme.trafficYellow.opacity(0.9))
            } else if !daemon.statusMessage.isEmpty {
                Text(daemon.statusMessage).font(.system(size: 11)).foregroundStyle(MethodTheme.textSecondary)
            }
            Spacer()
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(MethodTheme.background)
        .onAppear { daemon.refresh() }
        .animation(.easeOut(duration: 0.2), value: daemon.status)
    }

    @ViewBuilder
    private var daemonActionButton: some View {
        switch daemon.status {
        case .enabled:
            HStack(spacing: 10) {
                Label("Активен", systemImage: "checkmark.circle.fill")
                    .font(.system(size: 12))
                    .foregroundStyle(MethodTheme.connected)
                Button("Перезапустить") { daemon.reinstall() }
                    .buttonStyle(PressScale())
                    .font(.system(size: 11))
                    .disabled(daemon.isBusy)
            }
        case .requiresApproval:
            Button("Открыть Настройки") { daemon.openSystemSettingsLoginItems() }
                .buttonStyle(PressScale())
        default:
            Button("Активировать") { daemon.install() }
                .buttonStyle(PressScale())
                .disabled(daemon.isBusy)
        }
    }

    private var statusSubtitle: String {
        switch daemon.status {
        case .enabled:          return "Активен"
        case .requiresApproval: return "Ждёт подтверждения в Настройках системы"
        case .notFound:         return "Не установлен"
        case .notRegistered:    return "Не активирован"
        @unknown default:       return "Неизвестно"
        }
    }
}
