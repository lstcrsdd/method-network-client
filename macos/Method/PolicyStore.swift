import Foundation

/// Хранение пользовательских сценариев.
///
/// Сценарий — это `LanePolicy`: полосы и правила «что куда». Хранится
/// отдельно от профилей серверов, потому что живёт своей жизнью: серверы
/// меняются с подпиской, правила пишет человек и менять их за него нельзя.
///
/// Файл зашифрован тем же ключом, что и остальное: правила описывают, какие
/// сайты человек выводит мимо туннеля, — то есть и список сайтов, и сам факт
/// интереса к ним.
enum PolicyStore {

    private static var url: URL {
        let base = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Method", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        return base.appendingPathComponent("policy.dat")
    }

    /// Переносимое представление. Отдельно от `LanePolicy`, потому что у той
    /// есть перечисления со связанными значениями, а формат хранения обязан
    /// меняться медленнее модели.
    private struct Stored: Codable {
        struct Lane: Codable {
            var id: String
            var title: String
            var allowsDirect: Bool
            var justification: String?
            var axisIn: [String]?
            var minAxes: Int
        }
        struct Flow: Codable {
            /// «domainSuffix» | «domainKeyword» | «processName» | «port» | «private»
            var kind: String
            var values: [String]
            var lane: String
            var note: String?
        }
        var version: Int = 1
        var lanes: [Lane]
        var flows: [Flow]
        var defaultLane: String
    }

    static func load() -> LanePolicy {
        guard let sealed = try? Data(contentsOf: url),
              let raw = try? SecretStore.open(sealed),
              let s = try? JSONDecoder().decode(Stored.self, from: raw) else {
            return .factory()
        }
        let lanes = s.lanes.map { l in
            LanePolicy.Lane(
                id: l.id, title: l.title, allowsDirect: l.allowsDirect,
                justification: l.justification,
                axisIn: l.axisIn?.compactMap(LaneConfigBuilder.EvasionAxisKey.init(rawValue:)),
                minAxes: l.minAxes
            )
        }
        let flows: [LanePolicy.Flow] = s.flows.compactMap { f in
            let match: LanePolicy.Match
            switch f.kind {
            case "domainSuffix":  match = .domainSuffix(f.values)
            case "domainKeyword": match = .domainKeyword(f.values)
            case "processName":   match = .processName(f.values)
            case "port":          match = .port(f.values.compactMap(Int.init))
            case "private":       match = .ipIsPrivate
            default:              return nil
            }
            return LanePolicy.Flow(match: match, lane: f.lane, note: f.note)
        }
        let policy = LanePolicy(lanes: lanes, flows: flows, defaultLane: s.defaultLane)
        // Файл был написан прежним ключом — перекладываем на нынешний сразу,
        // иначе вопрос связки ключей повторится при следующем запуске.
        if SecretStore.lastOpenUsedLegacyKey { save(policy) }
        return policy
    }

    static func save(_ policy: LanePolicy) {
        let lanes = policy.lanes.map {
            Stored.Lane(id: $0.id, title: $0.title, allowsDirect: $0.allowsDirect,
                        justification: $0.justification,
                        axisIn: $0.axisIn?.map(\.rawValue), minAxes: $0.minAxes)
        }
        let flows: [Stored.Flow] = policy.flows.map { f in
            switch f.match {
            case .domainSuffix(let v):  return .init(kind: "domainSuffix", values: v, lane: f.lane, note: f.note)
            case .domainKeyword(let v): return .init(kind: "domainKeyword", values: v, lane: f.lane, note: f.note)
            case .processName(let v):   return .init(kind: "processName", values: v, lane: f.lane, note: f.note)
            case .port(let v):          return .init(kind: "port", values: v.map(String.init), lane: f.lane, note: f.note)
            case .ipIsPrivate:          return .init(kind: "private", values: [], lane: f.lane, note: f.note)
            }
        }
        let stored = Stored(lanes: lanes, flows: flows, defaultLane: policy.defaultLane)
        guard let raw = try? JSONEncoder().encode(stored),
              let sealed = try? SecretStore.seal(raw) else { return }
        try? sealed.write(to: url, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    static func reset() {
        try? FileManager.default.removeItem(at: url)
    }
}
