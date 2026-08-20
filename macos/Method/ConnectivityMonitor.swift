import Foundation
import Network

/// Проверяет РЕАЛЬНУЮ связность через туннель (а не просто факт «процесс поднят»).
/// Именно это отличает от Happ: при DPI/блокировке конкретного сервера туннель может
/// формально оставаться «подключен», но трафик не идёт — этот монитор такое ловит
/// и сигнализирует наверх, чтобы перезапустить ядро или сменить сервер.
///
/// Правило проекта: молчаливый отказ, выданный за успех, опаснее честной ошибки.
/// Поэтому монитор существует не ради красоты — статус «Подключено» не имеет
/// права стоять, когда трафик не идёт.
final class ConnectivityMonitor {
    /// Вызывается, когда подряд провалено `failureThreshold` проб.
    var onUnhealthy: (() -> Void)?

    private var timer: Timer?
    private var consecutiveFailures = 0
    /// Проба уже в полёте: при зависшей сети таймер иначе плодил бы запросы
    /// быстрее, чем они отваливаются по таймауту.
    private var probeInFlight = false
    private let failureThreshold = 3
    private let checkInterval: TimeInterval = 8
    private static let probeTimeout: TimeInterval = 5

    /// Адреса проб. Заданы строками и разбираются один раз: `URL(string:)!` на
    /// константе не падает сегодня, но это ровно тот образец, который потом
    /// копируют на строку из подписки — и вот он уже падает.
    private static let probeURLs: [URL] = [
        "https://www.gstatic.com/generate_204",
        "https://cp.cloudflare.com/generate_204",
    ].compactMap(URL.init(string:))

    private var probeIndex = 0

    /// Поколение наблюдения. Проба живёт до пяти секунд и вполне переживает
    /// `stop()`: без счётчика её запоздалый провал засчитывался бы уже НОВОЙ
    /// сессии — то есть свежий, только что поднятый туннель начинал бы жизнь с
    /// чужим отказом в активе и мог быть перезапущен на ровном месте.
    private var generation = 0

    /// Есть ли вообще сеть у машины. Без этого выключенный Wi-Fi выглядел бы
    /// как мёртвый туннель: пробы валятся, ядро перезапускается впустую и
    /// расходует лимит попыток, а в конце человек получает ложный диагноз
    /// «сервер не работает» вместо «нет сети».
    private let pathMonitor = NWPathMonitor()
    private var hasNetwork = true

    init() {
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let ok = path.status != .unsatisfied
            DispatchQueue.main.async { self?.hasNetwork = ok }
        }
        pathMonitor.start(queue: DispatchQueue(label: "network.method.client.path"))
    }

    func start() {
        stop()
        consecutiveFailures = 0
        generation &+= 1
        // .common — иначе таймер замирает, пока открыто меню в строке статуса,
        // и потеря связи не замечается, пока человек не закроет меню.
        let t = Timer(timeInterval: checkInterval, repeats: true) { [weak self] _ in self?.probe() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func stop() {
        timer?.invalidate(); timer = nil
        consecutiveFailures = 0
        probeInFlight = false
        generation &+= 1
    }

    /// Одиночная проба — тем же способом, каким монитор проверяет связность.
    /// Используется контроллером после перезапуска ядра: «поднялось» и
    /// «трафик пошёл» — разные вещи.
    /// - Parameter tryAll: перебрать все адреса, а не только первый.
    ///   Провал тогда засчитывается, только если не ответил НИ ОДИН — один
    ///   заблокированный домен не повод рвать рабочий туннель. При переборе
    ///   серверов на подключении это, наоборот, лишнее: там каждая лишняя
    ///   секунда умножается на число кандидатов.
    static func probeOnce(tryAll: Bool = true) async -> Bool {
        for url in (tryAll ? probeURLs : Array(probeURLs.prefix(1))) {
            var request = URLRequest(url: url, timeoutInterval: probeTimeout)
            request.cachePolicy = .reloadIgnoringLocalCacheData
            do {
                let (_, response) = try await URLSession.shared.data(for: request)
                if ((response as? HTTPURLResponse)?.statusCode ?? 500) < 500 { return true }
            } catch {
                continue
            }
        }
        return false
    }

    private func probe() {
        guard !probeInFlight, !Self.probeURLs.isEmpty else { return }
        guard hasNetwork else { consecutiveFailures = 0; return }

        let url = Self.probeURLs[probeIndex % Self.probeURLs.count]
        probeIndex = (probeIndex + 1) % Self.probeURLs.count
        probeInFlight = true

        var request = URLRequest(url: url, timeoutInterval: Self.probeTimeout)
        request.cachePolicy = .reloadIgnoringLocalCacheData
        let probeGeneration = generation
        let task = URLSession.shared.dataTask(with: request) { [weak self] _, response, error in
            let ok = error == nil && ((response as? HTTPURLResponse)?.statusCode ?? 0) < 500
            DispatchQueue.main.async {
                guard let self else { return }
                // Проба из прошлой сессии наблюдения — её итог ничего не значит.
                guard probeGeneration == self.generation else { return }
                self.probeInFlight = false
                // Сеть пропала уже во время пробы — это не вина туннеля.
                guard self.hasNetwork else { self.consecutiveFailures = 0; return }
                if ok {
                    self.consecutiveFailures = 0
                } else {
                    self.consecutiveFailures += 1
                    if self.consecutiveFailures >= self.failureThreshold {
                        self.consecutiveFailures = 0
                        self.onUnhealthy?()
                    }
                }
            }
        }
        task.resume()
    }

    deinit {
        timer?.invalidate()
        pathMonitor.cancel()
    }
}
