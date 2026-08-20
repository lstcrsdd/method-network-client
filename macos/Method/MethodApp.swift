import SwiftUI
import AppKit
import os.log

/// Ловит то, что иначе исчезает бесследно.
///
/// Отчётов о падениях у приложения нет ни одного — это хорошо, но означает
/// лишь, что мы ничего о них не знаем. Необработанное исключение Objective-C
/// (а из него растёт большинство падений AppKit: недопустимый аргумент, ответ
/// XPC дважды, обращение к освобождённому объекту) убивает процесс молча.
/// Здесь оно хотя бы попадает в системный журнал с описанием и стеком:
///   /usr/bin/log show --last 30m --predicate 'subsystem == "network.method.client"'
final class MethodAppDelegate: NSObject, NSApplicationDelegate {
    private static let log = OSLog(subsystem: "network.method.client", category: "Crash")

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSSetUncaughtExceptionHandler { exception in
            os_log("Необработанное исключение: %{public}@ — %{public}@\n%{public}@",
                   log: MethodAppDelegate.log, type: .fault,
                   exception.name.rawValue,
                   exception.reason ?? "без причины",
                   exception.callStackSymbols.joined(separator: "\n"))
        }
    }
}

@main
struct MethodApp: App {
    @NSApplicationDelegateAdaptor(MethodAppDelegate.self) private var appDelegate
    @StateObject private var controller = MethodController.shared

    var body: some Scene {
        WindowGroup {
            RootView()
                .onAppear {
                    NSApp.setActivationPolicy(.regular)
                    NSApp.activate(ignoringOtherApps: true)
                }
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 900, height: 620)

        Settings {
            MethodSettingsView()
        }

        MenuBarExtra {
            VStack(alignment: .leading) {
                Text(trayStatus)
                // Перезапуск ядра и смена сервера должны быть видны и тогда,
                // когда окно закрыто: иначе восстановление выглядит как
                // необъяснимый провал интернета.
                if let status = controller.connectAttemptStatus {
                    Text(status)
                } else if case .error(let message) = controller.state {
                    Text(message)
                }
                if let selected = controller.selected {
                    Text(selected.name)
                    Text(selected.protocol.displayName)
                } else {
                    Text("Нет конфигураций")
                }
            }
            Divider()
            Button(controller.isConnected ? "Отключить" : "Подключить") {
                controller.toggleConnection()
            }
            // Во время подключения кнопку НЕ блокируем: `toggleConnection`
            // трактует нажатие как отмену. Заблокированная кнопка означала,
            // что из строки статуса нельзя прервать затянувшуюся попытку —
            // единственным выходом оставалось открыть окно.
            .disabled(controller.selected == nil)
            SettingsLink {
                Text("Настройки…")
            }
            Divider()
            Button("Показать Method") {
                NSApp.setActivationPolicy(.regular)
                NSApp.activate(ignoringOtherApps: true)
            }
            Button("Выйти") {
                NSApp.terminate(nil)
            }
        } label: {
            Image(systemName: trayIcon)
        }
        .menuBarExtraStyle(.menu)
    }

    private var trayStatus: String {
        switch controller.state {
        case .connected: return "Method подключён"
        case .connecting: return "Method подключается…"
        case .disconnected: return "Method отключён"
        case .error: return "Method: ошибка"
        }
    }

    private var trayIcon: String {
        switch controller.state {
        case .connected: return "link.circle.fill"
        case .connecting: return "arrow.triangle.2.circlepath.circle"
        case .error: return "exclamationmark.circle"
        case .disconnected: return "link.circle"
        }
    }
}
