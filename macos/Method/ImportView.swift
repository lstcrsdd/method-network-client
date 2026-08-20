import SwiftUI

/// Лист добавления конфигураций: ссылки (hysteria2://, vless://) и/или
/// ссылки-подписки (http/https, отдают список серверов), по одной в строке.
struct ImportView: View {
    @ObservedObject var controller: MethodController
    @Environment(\.dismiss) private var dismiss

    @State private var text = ""
    @State private var errors: [String] = []
    @State private var addedNote: String?
    @State private var isLoading = false

    private var isEmpty: Bool { text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("Добавить конфигурацию")
                    .font(.system(size: 18, weight: .bold))
                    .foregroundStyle(MethodTheme.textPrimary)
                Spacer()
                Button { dismiss() } label: {
                    Image(systemName: "xmark").font(.system(size: 12, weight: .bold))
                        .foregroundStyle(MethodTheme.textSecondary)
                }.buttonStyle(.plain)
            }

            Text("Вставьте ссылку hysteria2://, vless:// или ссылку-подписку (https://…) — можно несколько, по одной в строке.")
                .font(.system(size: 12))
                .foregroundStyle(MethodTheme.textSecondary)

            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(Color.white.opacity(0.04))
                    .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .strokeBorder(MethodTheme.glassStroke, lineWidth: 0.5))
                if text.isEmpty {
                    Text("hysteria2://…\nvless://…\nhttps://sub.example.com/…")
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(MethodTheme.textMuted)
                        .padding(.horizontal, 12).padding(.vertical, 10)
                        .allowsHitTesting(false)
                }
                TextEditor(text: $text)
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundStyle(Color.white.opacity(0.9))
                    .scrollContentBackground(.hidden)
                    .padding(.horizontal, 8).padding(.vertical, 6)
                    .disabled(isLoading)
            }
            .frame(height: 150)

            if let addedNote {
                Text(addedNote).font(.system(size: 12, weight: .medium))
                    .foregroundStyle(MethodTheme.connected)
            }
            if !errors.isEmpty {
                VStack(alignment: .leading, spacing: 3) {
                    ForEach(errors, id: \.self) { e in
                        Text("• " + e).font(.system(size: 11))
                            .foregroundStyle(MethodTheme.trafficYellow.opacity(0.9))
                    }
                }
            }

            HStack {
                Button {
                    if let s = NSPasteboard.general.string(forType: .string) { text = s }
                } label: { Label("Вставить из буфера", systemImage: "doc.on.clipboard") }
                    .buttonStyle(PressScale())
                    .font(.system(size: 12))
                    .foregroundStyle(MethodTheme.textSecondary)
                    .disabled(isLoading)
                Spacer()
                Button { Task { await add() } } label: {
                    if isLoading {
                        ProgressView().controlSize(.small)
                            .frame(width: 76, height: 18)
                    } else {
                        Text("Добавить")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(MethodTheme.background)
                            .padding(.horizontal, 20).padding(.vertical, 9)
                            .background(Capsule().fill(isEmpty ? Color.white.opacity(0.3) : .white))
                    }
                }
                .buttonStyle(PressScale())
                .disabled(isEmpty || isLoading)
            }
        }
        .padding(24)
        .frame(width: 480)
        .background(MethodTheme.background)
    }

    private func add() async {
        isLoading = true
        errors = []
        let outcome = await controller.addFromInput(text)
        isLoading = false
        errors = outcome.errors
        let totalAdded = outcome.addedProfiles + outcome.addedSubscriptions
        if totalAdded > 0 {
            var parts: [String] = []
            if outcome.addedSubscriptions > 0 { parts.append("подписок: \(outcome.addedSubscriptions)") }
            if outcome.addedProfiles > 0 { parts.append("серверов: \(outcome.addedProfiles)") }
            addedNote = "Добавлено — " + parts.joined(separator: ", ")
            if outcome.errors.isEmpty {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { dismiss() }
            } else {
                text = ""
            }
        } else if outcome.errors.isEmpty {
            addedNote = "Ничего не добавлено (дубликаты?)"
        } else {
            addedNote = nil
        }
    }
}
