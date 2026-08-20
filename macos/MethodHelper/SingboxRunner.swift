import Foundation
import os.log

/// Запуск/супервизор дочернего sing-box для приложения Method.
/// Своя рабочая папка `/Library/Application Support/Method`, чтобы не пересекаться
/// с Method VPN.
class SingboxRunner {
    static let shared = SingboxRunner()
    private let logger = OSLog(subsystem: "network.method.client.helper", category: "SingboxRunner")

    /// Пишется только из `queue`, а читается ещё и с потока XPC (сторож ядра в
    /// приложении спрашивает `isTunnelRunning` каждые шесть секунд). Голая
    /// `var` здесь — гонка на ссылке на объект, то есть не «неверный ответ», а
    /// падение root-демона вместе с управлением уже поднятым туннелем.
    ///
    /// Замок, а не `queue.sync`: очередь бывает занята до пятнадцати секунд
    /// (`sing-box check`), и ответ на безобидный вопрос «жив ли процесс» ждал
    /// бы всё это время, копя заблокированные потоки XPC.
    private var _process: Process?
    private let processLock = NSLock()
    private var process: Process? {
        get { processLock.lock(); defer { processLock.unlock() }; return _process }
        set { processLock.lock(); _process = newValue; processLock.unlock() }
    }
    /// Пайп живого процесса. Держим ссылку, чтобы снять обработчик чтения при
    /// остановке: брошенный `readabilityHandler` после смерти процесса
    /// вызывается снова и снова с пустыми данными — это ровный 100% CPU в
    /// root-демоне и утечка дескрипторов на каждом переподключении.
    private var outputPipe: Pipe?
    private var lastConfig: String?
    private var lastExecutableURL: URL?
    private let workDirectory: URL
    private let configFileURL: URL
    private let queue = DispatchQueue(label: "network.method.client.helper.singbox")

    /// Сроки ожидания дочерних процессов. Все три случая уже наблюдались в
    /// природе как «приложение висит на Подключаемся…»: без них демон блокирует
    /// свою XPC-очередь навсегда, а клиенту нечего ждать — ответа не будет.
    private static let checkTimeout: TimeInterval = 15
    private static let terminateTimeout: TimeInterval = 5
    private static let killTimeout: TimeInterval = 3

    // Хвост stdout/stderr sing-box — чтобы вернуть вызывающему реальную причину
    // падения, а не молчаливое "не удалось подключиться".
    private var recentOutput: [String] = []
    private let outputLock = NSLock()
    private let maxOutputLines = 20

    // Защита от бесконечного тайт-лупа автоперезапуска при неисправимой ошибке
    // конфигурации (тот же антипаттерн, что чинили на серверах: без лимита один
    // и тот же процесс мог бы падать и рестартовать сотни раз в секунду).
    private var restartTimestamps: [Date] = []
    private static let maxRestartsInWindow = 5
    private static let restartWindow: TimeInterval = 30

    /// Номер текущего запуска. Обработчик завершения помнит СВОЙ номер и
    /// трогает состояние, только если номер всё ещё актуален.
    ///
    /// Что закрывает. Обработчик завершения ставит свою работу в очередь
    /// (`queue.async`), то есть исполняется ПОЗЖЕ момента смерти процесса — а
    /// за это время `stopLocked` или новый `startLocked` уже могли пройти.
    /// Тогда старый обработчик, ничего не зная, обнулял `process` (уже чужой,
    /// живой — и ядро начинало числиться мёртвым) или поднимал sing-box заново
    /// от root уже ПОСЛЕ остановки: приложение показывает «Не подключено», а
    /// весь трафик машины по-прежнему идёт в туннель. Ровно тот случай, что в
    /// правилах проекта назван опаснее честной ошибки.
    ///
    /// Честно: окно узкое (десятки микросекунд — между тем, как ядро реапнуто,
    /// и тем, как блок попал в очередь), воспроизвести его в
    /// `Scripts/core_supervisor_test.sh` не удалось. Счётчик закрывает его по
    /// построению, а не по везению с расписанием потоков.
    private var runGeneration = 0

    var isRunning: Bool { process?.isRunning == true }

    init() {
        let appSupport = URL(fileURLWithPath: "/Library/Application Support/Method")
        self.workDirectory = appSupport
        self.configFileURL = appSupport.appendingPathComponent("config.json")
        if !FileManager.default.fileExists(atPath: workDirectory.path) {
            // 0700: в конфиге лежат пароли и ключи всех серверов, а каталог
            // общесистемный — по умолчанию его прочитал бы любой пользователь.
            try? FileManager.default.createDirectory(
                at: workDirectory,
                withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700]
            )
        }
    }

    /// Запускает sing-box и коротко ждёт, чтобы поймать мгновенный крash (битый
    /// конфиг, недоступный TUN и т.п.) ДО ответа вызывающему — иначе клиент увидит
    /// «подключено»/«подключение», хотя процесс уже умер.
    func start(config: String, executableURL: URL) throws {
        try queue.sync {
            stopLocked()
            resetOutput()
            lastConfig = config
            lastExecutableURL = executableURL
            try startLocked(config: config, executableURL: executableURL)
            try assertSurvivedStartup()
        }
    }

    /// Ждёт короткое время и проверяет, что процесс не умер сразу.
    private func assertSurvivedStartup() throws {
        Thread.sleep(forTimeInterval: 0.8)
        guard let task = process, !task.isRunning else { return }
        let code = task.terminationStatus
        let output = snapshotOutput()
        process = nil
        if let pipe = outputPipe {
            pipe.fileHandleForReading.readabilityHandler = nil
            try? pipe.fileHandleForReading.close()
            outputPipe = nil
        }
        os_log("sing-box exited immediately, code=%d output=%{public}@",
               log: logger, type: .error, code, output)
        throw NSError(domain: "SingboxRunner", code: 2, userInfo: [
            NSLocalizedDescriptionKey: output.isEmpty
                ? "sing-box завершился сразу после запуска (код \(code))"
                : output,
        ])
    }

    func stop() { queue.sync { stopLocked() } }

    func reconnect() throws {
        try queue.sync {
            guard let config = lastConfig, let exe = lastExecutableURL else {
                throw NSError(domain: "SingboxRunner", code: 1, userInfo: [
                    NSLocalizedDescriptionKey: "Нет сохранённого конфига для переподключения",
                ])
            }
            stopLocked()
            resetOutput()
            try startLocked(config: config, executableURL: exe)
            // Та же проверка, что и при первом запуске. Без неё перезапуск
            // отвечал «успех» на умерший через полсекунды процесс, и клиент
            // рисовал «Подключено» поверх мёртвого туннеля.
            try assertSurvivedStartup()
        }
    }

    private func startLocked(config: String, executableURL: URL) throws {
        runGeneration &+= 1
        let generation = runGeneration
        try config.write(to: configFileURL, atomically: true, encoding: .utf8)
        // atomically: true пересоздаёт файл, поэтому права ставим каждый раз.
        try? FileManager.default.setAttributes(
            [.posixPermissions: 0o600], ofItemAtPath: configFileURL.path
        )
        try checkConfig(executableURL: executableURL)

        let task = Process()
        task.executableURL = executableURL
        task.arguments = ["run", "-c", configFileURL.path]
        task.currentDirectoryURL = workDirectory
        let startedAt = Date()

        let pipe = Pipe()
        task.standardOutput = pipe
        task.standardError = pipe
        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            // Пустые данные = конец файла. Обработчик обязан сняться сам,
            // иначе он будет вызываться в цикле без остановки.
            guard !data.isEmpty else {
                handle.readabilityHandler = nil
                return
            }
            if let str = String(data: data, encoding: .utf8) {
                let trimmed = str.trimmingCharacters(in: .newlines)
                os_log("sing-box: %{public}@", log: self?.logger ?? .default, type: .debug, trimmed)
                self?.appendOutput(trimmed)
            }
        }
        outputPipe = pipe

        task.terminationHandler = { [weak self] proc in
            guard let self else { return }
            os_log("sing-box terminated: %d", log: self.logger, type: proc.terminationStatus == 0 ? .default : .error,
                   proc.terminationStatus)
            self.queue.async {
                // Чужое поколение: туннель уже остановлен или перезапущен, а
                // этот обработчик — эхо прошлого процесса. Ни трогать `process`
                // (там уже новый), ни тем более перезапускать нельзя.
                guard generation == self.runGeneration else { return }
                self.process = nil
                guard proc.terminationStatus != 0,
                      Date().timeIntervalSince(startedAt) > 1.5,
                      let config = self.lastConfig, let exe = self.lastExecutableURL else { return }
                guard self.shouldAutoRestart() else {
                    os_log("Слишком много падений подряд — автоперезапуск остановлен",
                           log: self.logger, type: .error)
                    return
                }
                os_log("Auto-restarting sing-box…", log: self.logger, type: .default)
                try? self.startLocked(config: config, executableURL: exe)
            }
        }

        try task.run()
        process = task
        os_log("sing-box started PID=%d", log: logger, type: .default, task.processIdentifier)
    }

    /// Проверка конфига дочерним `sing-box check`.
    ///
    /// Два подводных камня, оба ведут к вечному зависанию демона (а значит —
    /// приложения, которое ждёт ответа по XPC):
    ///  1. `waitUntilExit()` до чтения пайпа. Пайп ёмкостью 64 КБ; если ядро
    ///     напишет больше (а на битом конфиге из подписки диагностика бывает
    ///     длинной), оно заблокируется на write, мы — на wait, и это навсегда.
    ///     Поэтому читаем ПАРАЛЛЕЛЬНО, обработчиком.
    ///  2. Дочерний процесс может не завершиться вовсе. Поэтому — срок и SIGKILL.
    private func checkConfig(executableURL: URL) throws {
        let task = Process()
        task.executableURL = executableURL
        task.arguments = ["check", "-c", configFileURL.path]
        task.currentDirectoryURL = workDirectory

        let pipe = Pipe()
        task.standardOutput = pipe
        task.standardError = pipe

        var collected = Data()
        let collectLock = NSLock()
        let handle = pipe.fileHandleForReading
        handle.readabilityHandler = { h in
            let chunk = h.availableData
            guard !chunk.isEmpty else { h.readabilityHandler = nil; return }
            collectLock.lock(); collected.append(chunk); collectLock.unlock()
        }

        try task.run()
        let finished = waitForExit(task, timeout: Self.checkTimeout)
        if !finished { hardKill(task) }
        handle.readabilityHandler = nil
        try? handle.close()

        collectLock.lock()
        let output = String(data: collected, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        collectLock.unlock()

        if !output.isEmpty {
            appendOutput(output)
            os_log("sing-box check: %{public}@", log: logger, type: .debug, output)
        }

        guard finished else {
            throw NSError(domain: "SingboxRunner", code: 4, userInfo: [
                NSLocalizedDescriptionKey:
                    "sing-box не ответил на проверку конфигурации за \(Int(Self.checkTimeout)) с — ядро остановлено.",
            ])
        }
        guard task.terminationStatus == 0 else {
            throw NSError(domain: "SingboxRunner", code: 3, userInfo: [
                NSLocalizedDescriptionKey: output.isEmpty
                    ? "sing-box check завершился с кодом \(task.terminationStatus)"
                    : output,
            ])
        }
    }

    private func stopLocked() {
        // Смена поколения обесценивает обработчики завершения всех прошлых
        // процессов: остановка означает остановку, а не «перезапустить, если
        // успеет проснуться очередь».
        runGeneration &+= 1
        restartTimestamps.removeAll()
        // Обработчик снимаем всегда, даже если процесс уже мёртв: иначе он
        // остаётся висеть на закрытом дескрипторе.
        if let pipe = outputPipe {
            pipe.fileHandleForReading.readabilityHandler = nil
            try? pipe.fileHandleForReading.close()
            outputPipe = nil
        }
        guard let task = process, task.isRunning else { process = nil; return }
        os_log("Stopping sing-box…", log: logger, type: .default)
        task.terminationHandler = nil
        task.terminate()
        // Раньше здесь стоял `waitUntilExit()` без срока. Если ядро зависает на
        // разборке utun (а это ровно тот случай, ради которого его и
        // останавливают), демон не возвращается из stopTunnel никогда, и у
        // человека намертво залипает кнопка «Отключить».
        if !waitForExit(task, timeout: Self.terminateTimeout) {
            os_log("sing-box не завершился по SIGTERM — SIGKILL", log: logger, type: .error)
            hardKill(task)
        }
        process = nil
    }

    /// Ждёт завершения не дольше срока. `true` — процесс завершился сам.
    private func waitForExit(_ task: Process, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while task.isRunning && Date() < deadline {
            usleep(50_000)
        }
        return !task.isRunning
    }

    private func hardKill(_ task: Process) {
        guard task.isRunning else { return }
        kill(task.processIdentifier, SIGKILL)
        _ = waitForExit(task, timeout: Self.killTimeout)
    }

    /// true — можно перезапускать; false — превышен лимит попыток за окно времени.
    private func shouldAutoRestart() -> Bool {
        let now = Date()
        restartTimestamps.append(now)
        restartTimestamps.removeAll { now.timeIntervalSince($0) > Self.restartWindow }
        return restartTimestamps.count <= Self.maxRestartsInWindow
    }

    private func resetOutput() {
        outputLock.lock(); recentOutput.removeAll(); outputLock.unlock()
    }

    private func appendOutput(_ line: String) {
        outputLock.lock()
        recentOutput.append(line)
        if recentOutput.count > maxOutputLines { recentOutput.removeFirst() }
        outputLock.unlock()
    }

    private func snapshotOutput() -> String {
        outputLock.lock(); defer { outputLock.unlock() }
        return recentOutput.joined(separator: "\n")
    }
}
