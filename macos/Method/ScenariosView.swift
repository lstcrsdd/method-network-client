import SwiftUI

/// Экран сценариев: какой трафик каким путём идёт.
///
/// Правило называет ПОЛОСУ, а не сервер. «Банк — напрямую», «игры — по самому
/// ровному пути»: какой маршрут сегодня удовлетворяет требование, решает
/// движок. Поэтому правила не приходится переписывать при смене нод, а смена
/// маршрута не трогает правила.
struct ScenariosView: View {
    @ObservedObject var controller: MethodController
    @State private var showAdd = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text("Сценарии")
                    .font(.system(size: 26, weight: .bold))
                    .foregroundStyle(MethodTheme.textPrimary)
                Spacer()
                Button { showAdd = true } label: {
                    Image(systemName: "plus")
                        .font(.system(size: 12, weight: .bold))
                        .foregroundStyle(Color.white.opacity(0.8))
                        .frame(width: 28, height: 28)
                        .background(Circle().fill(MethodTheme.surface)
                            .overlay(Circle().strokeBorder(MethodTheme.glassStroke, lineWidth: 0.5)))
                }
                .buttonStyle(.plain)
            }
            .padding(.bottom, 6)

            Text("Правило называет путь, а не сервер. Какой маршрут ему сегодня "
                 + "соответствует — решает клиент сам.")
                .font(.system(size: 12))
                .foregroundStyle(MethodTheme.textSecondary)
                .padding(.bottom, 18)

            // Правила попадают в конфиг ядра, а он собирается при
            // подключении. Пока не переподключились — список и поведение
            // расходятся, и сказать об этом обязан клиент, а не журнал.
            if controller.policyPendingRestart {
                HStack(spacing: 10) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 12))
                        .foregroundStyle(MethodTheme.trafficYellow)
                    Text("Правила изменены. Пока не переподключитесь, трафик идёт по прежним.")
                        .font(.system(size: 12))
                        .foregroundStyle(Color.white.opacity(0.85))
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 8)
                    Button("Применить") { controller.reconnectForPolicy() }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
                }
                .padding(.horizontal, 14).padding(.vertical, 10)
                .background {
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(MethodTheme.trafficYellow.opacity(0.10))
                        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
                            .strokeBorder(MethodTheme.trafficYellow.opacity(0.35), lineWidth: 0.8))
                }
                .padding(.bottom, 12)
            }

            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(controller.policy.lanes, id: \.id) { lane in
                        laneCard(lane)
                    }
                }
            }
        }
        .padding(36)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .sheet(isPresented: $showAdd) {
            AddRuleSheet(controller: controller, isPresented: $showAdd)
        }
    }

    private func laneCard(_ lane: LanePolicy.Lane) -> some View {
        // Перекрытые правила надо показывать перечёркнутыми: строка в списке
        // без пометки читается как действующая, а она не срабатывает никогда.
        let shadowed = Dictionary(uniqueKeysWithValues:
            controller.policy.shadowedFlows().map { ($0.index, $0.by) })
        let indexed = controller.policy.flows.enumerated()
            .filter { $0.element.lane == lane.id }
        let rules = indexed.map(\.element)
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: lane.allowsDirect ? "arrow.up.right" : "lock.fill")
                    .font(.system(size: 11))
                    .foregroundStyle(lane.allowsDirect ? MethodTheme.trafficYellow : MethodTheme.connected)
                Text(lane.title)
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(MethodTheme.textPrimary)
                if lane.id == controller.policy.defaultLane {
                    Text("всё остальное")
                        .font(.system(size: 10, weight: .medium))
                        .foregroundStyle(MethodTheme.textMuted)
                        .padding(.horizontal, 6).padding(.vertical, 2)
                        .background(Capsule().fill(Color.white.opacity(0.06)))
                }
                Spacer()
                Text(lane.allowsDirect ? "мимо туннеля" : "через туннель")
                    .font(.system(size: 11))
                    .foregroundStyle(MethodTheme.textSecondary)
            }
            .padding(.horizontal, 14).padding(.top, 12).padding(.bottom, 8)

            // Объяснение «зачем» показывается всегда, когда полоса выпускает
            // трафик открытым. Это не формальность: через месяц никто не
            // вспомнит, почему банк ходит мимо туннеля.
            if lane.allowsDirect, let why = lane.justification, !why.isEmpty {
                Text(why)
                    .font(.system(size: 11))
                    .foregroundStyle(MethodTheme.trafficYellow.opacity(0.8))
                    .padding(.horizontal, 14).padding(.bottom, 8)
            }

            if !rules.isEmpty {
                Divider().overlay(Color.white.opacity(0.06))
                VStack(spacing: 0) {
                    ForEach(indexed, id: \.offset) { pair in
                        let rule = pair.element
                        let coveredBy = shadowed[pair.offset]
                        HStack(spacing: 8) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(describe(rule.match))
                                    .font(.system(size: 12))
                                    .foregroundStyle(Color.white.opacity(coveredBy == nil ? 0.85 : 0.4))
                                    .strikethrough(coveredBy != nil, color: MethodTheme.trafficYellow)
                                    .lineLimit(2)
                                if let by = coveredBy {
                                    Text("не сработает: выше уже стоит «"
                                         + describe(controller.policy.flows[by].match) + "»")
                                        .font(.system(size: 10))
                                        .foregroundStyle(MethodTheme.trafficYellow.opacity(0.85))
                                        .fixedSize(horizontal: false, vertical: true)
                                }
                            }
                            Spacer(minLength: 8)
                            Button {
                                controller.removeRule(rule)
                            } label: {
                                Image(systemName: "xmark.circle.fill")
                                    .font(.system(size: 12))
                                    .foregroundStyle(MethodTheme.textMuted)
                            }
                            .buttonStyle(.plain)
                            .help("Удалить правило")
                        }
                        .padding(.horizontal, 14).padding(.vertical, 8)
                    }
                }
            }
        }
        .background {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(MethodTheme.surface)
                .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(MethodTheme.glassStroke, lineWidth: 0.6))
        }
    }

    private func describe(_ m: LanePolicy.Match) -> String {
        switch m {
        case .domainSuffix(let d):   return "сайты: " + d.joined(separator: ", ")
        case .domainKeyword(let d):  return "в адресе есть: " + d.joined(separator: ", ")
        case .processName(let p):    return "приложения: " + p.joined(separator: ", ")
        case .port(let p):           return "порты: " + p.map(String.init).joined(separator: ", ")
        case .ipIsPrivate:           return "локальная сеть"
        }
    }
}

/// Добавление правила. Одно поле и один выбор — больше для правила не нужно.
struct AddRuleSheet: View {
    @ObservedObject var controller: MethodController
    @Binding var isPresented: Bool

    @State private var domains = ""
    @State private var laneID = ""
    @State private var justification = ""
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Новое правило")
                .font(.system(size: 17, weight: .semibold))
                .foregroundStyle(MethodTheme.textPrimary)

            VStack(alignment: .leading, spacing: 6) {
                Text("Сайты").font(.system(size: 11)).foregroundStyle(MethodTheme.textSecondary)
                TextField("например: tinkoff.ru, sberbank.ru", text: $domains)
                    .textFieldStyle(.roundedBorder)
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Каким путём").font(.system(size: 11)).foregroundStyle(MethodTheme.textSecondary)
                Picker("", selection: $laneID) {
                    ForEach(controller.policy.lanes, id: \.id) { lane in
                        Text(lane.title + (lane.allowsDirect ? " — мимо туннеля" : "")).tag(lane.id)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
            }

            // Объяснение обязательно только для прямого выхода: именно там
            // человек через месяц не вспомнит, зачем он это сделал, а цена
            // ошибки — раскрытый адрес.
            if selectedLaneAllowsDirect {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Зачем мимо туннеля")
                        .font(.system(size: 11)).foregroundStyle(MethodTheme.trafficYellow)
                    TextField("например: антифрод рвёт сессию при смене адреса",
                              text: $justification)
                        .textFieldStyle(.roundedBorder)
                    Text("Этот трафик пойдёт с вашим настоящим адресом.")
                        .font(.system(size: 10)).foregroundStyle(MethodTheme.textMuted)
                }
            }

            if let error {
                Text(error)
                    .font(.system(size: 11))
                    .foregroundStyle(MethodTheme.trafficRed)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button("Отмена") { isPresented = false }
                Button("Добавить") { add() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(domains.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(22)
        .frame(width: 440)
        .onAppear { if laneID.isEmpty { laneID = controller.policy.lanes.first?.id ?? "" } }
    }

    private var selectedLaneAllowsDirect: Bool {
        controller.policy.lanes.first { $0.id == laneID }?.allowsDirect ?? false
    }

    private func add() {
        let list = domains.split(whereSeparator: { ", ".contains($0) })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        guard !list.isEmpty else { return }
        if let message = controller.addRule(domains: list, lane: laneID,
                                            justification: justification) {
            error = message
            return
        }
        isPresented = false
    }
}
