import Foundation

enum AppBundleLocator {
    /// Поднимается от бинарника демона к корню .app.
    static func appBundleURL(from helperExecutable: URL) -> URL? {
        var url = helperExecutable.deletingLastPathComponent()
        for _ in 0..<5 {
            let infoPlist = url.appendingPathComponent("Contents/Info.plist")
            if FileManager.default.fileExists(atPath: infoPlist.path) {
                return url
            }
            url.deleteLastPathComponent()
        }
        return nil
    }

    static func singBoxURL(from helperExecutable: URL) -> URL? {
        guard let app = appBundleURL(from: helperExecutable) else { return nil }
        let url = app.appendingPathComponent("Contents/Resources/sing-box")
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }
}
