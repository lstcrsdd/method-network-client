// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Method.Core;
using Method.Core.Tunnel;

namespace Method.App.Services;

/// <summary>
/// Привилегированная часть: режим <c>--service</c>, работает под SYSTEM. Держит
/// именованный канал, запускает движок и надзирает за ним.
///
/// <para>Права даёт служба Windows <c>MethodTunnel</c>: она запускает тот же
/// <c>Method.exe</c> с аргументом <c>--service</c> под учётной записью SYSTEM.
/// Сама эта часть про службу ничего не знает — она получает отмену и по ней
/// сворачивается, поэтому её можно гонять и в консоли (<c>--service --debug</c>),
/// где отмену даёт Ctrl+C.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class TunnelHost
{
    /// <summary>
    /// Ворота вокруг операций, меняющих состояние туннеля. Не <c>lock</c>, потому
    /// что внутри ждут: остановка прежнего процесса, уборка адаптера, проверка
    /// конфигурации.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// volatile: состояние туннеля читается для ответа на «status» без ожидания
    /// ворот — иначе долгий запуск подвешивал бы опрос, и интерфейс показывал бы
    /// ложное «помощник не запущен».
    /// </summary>
    private static volatile Process? _engine;

    private static string? _lastConfig;
    private static bool _intentionalStop;
    private static bool _killSwitch;
    private static bool _blockLanDns;

    private static readonly HelperLog Log = new();

    /// <summary>
    /// Сколько одновременных соединений принимает канал. Однопоточный сервер на
    /// одно соединение вешался целиком, если обработка запуска подвисала: опрос
    /// состояния переставал отвечать вместе с ней.
    /// </summary>
    private const int MaxPipeInstances = 16;

    // Надзор за движком: экспоненциальная пауза вместо мгновенного цикла.
    private static int _restartCount;
    private static DateTime _lastStartUtc;
    private const int MaxRestartBackoffMs = 15_000;

    /// <summary>Пауза при затяжном отказе: попытки продолжаются, но редко.</summary>
    private const int LongRestartBackoffMs = 60_000;

    private static readonly TimeSpan StableRunThreshold = TimeSpan.FromSeconds(30);

    private static string WorkDir => AppPaths.ProgramDataDir;
    private static string EnginePath => AppPaths.EngineFile;
    private static string WintunPath => Path.Combine(WorkDir, "wintun.dll");
    private static string ConfigPath => AppPaths.ConfigFile;

    /// <param name="token">
    /// Отмена приходит от диспетчера служб: «остановить» и «машина выключается».
    /// По ней цикл канала выходит, движок гасится, блокировка снимается. Без
    /// отмены остановка службы была бы убийством процесса, а убитый держатель
    /// туннеля оставляет за собой блокировку исходящего трафика — то есть машину
    /// без сети.
    /// </param>
    public static async Task<int> RunAsync(CancellationToken token = default)
    {
        // Проверяем права первым делом. Без них не выйдет ничего: ни создать
        // адаптер, ни поправить маршруты, ни даже писать в собственный рабочий
        // каталог после урезания прав. Отказаться сразу и вслух честнее, чем
        // дойти до середины и упасть с невнятной причиной — так и вышло в первый
        // раз, когда права урезались раньше проверки и журнал замолчал вместе с
        // объяснением.
        if (!Elevation.IsElevated())
        {
            const string message =
                "привилегированная часть запущена без прав администратора. " +
                "Так она работать не может: адаптер туннеля создаётся только с правами. " +
                "Обычно её запускает служба Method от SYSTEM.";
            Console.Error.WriteLine("Method: " + message);
            Log.Write(message);
            SystemJournal.Error(SystemJournal.Id.HostFailed, "Method: " + message);
            return 78;
        }

        try
        {
            Directory.CreateDirectory(WorkDir);
            Log.Write($"запуск привилегированной части (пользователь {Environment.UserName})");

            // Права урезаются до извлечения движка, чтобы он лёг уже в закрытый
            // каталог. Делается это на каждом запуске, а не только при установке:
            // права могли изменить, а каталог, из которого SYSTEM запускает
            // исполняемый файл, обязан быть недоступен на запись обычному
            // пользователю — иначе это готовое повышение привилегий.
            var hardenProblem = SecureDirectory.Harden(WorkDir);
            if (hardenProblem is not null)
                Log.Write($"ВНИМАНИЕ: права на рабочий каталог не урезаны — {hardenProblem}");

            // Первым делом снимаем возможную блокировку, оставшуюся от падения или
            // перезагрузки, — до всего остального. Если делать это после извлечения
            // движка, то любая ошибка извлечения (файл занят, карантин антивируса,
            // нет места) выбросит исключение раньше, блокировка переживёт
            // перезагрузку, и машина останется без сети до вмешательства руками.
            // Страховочное действие не должно зависеть от успеха ненадёжного кода.
            ClearLeftoverProtection();

            var installDir = Path.GetDirectoryName(Environment.ProcessPath) ?? WorkDir;

            if (!EmbeddedPayload.ExtractWithFallback("sing-box.exe", EnginePath,
                    Path.Combine(installDir, "sing-box.exe")))
                throw new FileNotFoundException("движок sing-box.exe не найден ни в сборке, ни рядом с ней");

            if (!EmbeddedPayload.ExtractWithFallback("wintun.dll", WintunPath,
                    Path.Combine(installDir, "wintun.dll")))
                throw new FileNotFoundException("wintun.dll не найден ни в сборке, ни рядом с ней");

            // Объект задания создаётся до запуска любых процессов и живёт столько
            // же, сколько мы: при нештатной смерти Windows сама погасит движок.
            // Неудача не блокирует старт — туннель важнее страховки.
            ProcessJobGuard.EnsureCreated(Log.Write);

            Log.Write("готов, слушаю канал");
            await ServeLoopAsync(token).ConfigureAwait(false);

            // Уборка здесь, а не в вызывающем: цикл канала выходит только по
            // отмене, и всякий, кто нас остановил, обязан получить машину в том же
            // виде, в каком взял, — без движка, без адаптера и без блокировки.
            await ShutdownAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception e)
        {
            Log.Write($"неустранимая ошибка: {e}");

            SystemJournal.Error(SystemJournal.Id.HostFailed,
                $"Привилегированная часть Method не смогла работать: {e.Message}. " +
                "Туннель не поднимется, пока причина не устранена.");

            return 1;
        }
    }

    /// <summary>
    /// Свернуться начисто: погасить движок, убрать адаптер, снять блокировку.
    ///
    /// <para>Вызывается на остановке службы и на выключении машины. Порядок здесь
    /// важнее краткости: сперва снимается намерение поднимать движок заново
    /// (иначе надзор поднимет его прямо во время остановки), затем гасится сам
    /// движок, и только потом снимается блокировка. Снять блокировку раньше —
    /// значит на несколько секунд выпустить трафик мимо ещё живого туннеля.</para>
    ///
    /// <para>Блокировка снимается в любом случае, даже если движок погасить не
    /// удалось. Здесь мы уходим, и оставленная блокировка означает машину без
    /// сети до тех пор, пока человек не догадается запустить
    /// <c>--firewall-reset</c>.</para>
    /// </summary>
    public static async Task ShutdownAsync()
    {
        Log.Write("остановка привилегированной части");

        // Ставим до ворот: надзор проверяет этот флаг, а ждать ворота он может
        // дольше, чем у нас есть времени на остановку службы.
        _intentionalStop = true;
        _lastConfig = null;

        // Ворота ждём ограниченно. Диспетчер служб не ждёт вечно, и лучше
        // погасить движок поверх чужой затянувшейся операции, чем быть убитыми
        // на середине с поднятой блокировкой.
        var gateTaken = await Gate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (!gateTaken) Log.Write("ворота заняты дольше десяти секунд — гашу движок поверх");

        try
        {
            await StopEngineAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Write($"остановка движка при завершении: {e.Message}");
        }
        finally
        {
            if (gateTaken) Gate.Release();
        }

        ClearLeftoverProtection();
        Log.Write("привилегированная часть остановлена");
    }

    private static void ClearLeftoverProtection()
    {
        try { KillSwitch.Disable(); }
        catch (Exception e) { Log.Write($"не удалось снять kill switch на старте: {e.Message}"); }

        // Блокировка IPv6 переживает падение ровно так же и точно так же оставляет
        // человека с наполовину отрезанной сетью.
        try { KillSwitch.UnblockIpv6(); }
        catch (Exception e) { Log.Write($"не удалось снять блокировку IPv6 на старте: {e.Message}"); }

        try { KillSwitch.UnblockLanDns(); }
        catch (Exception e) { Log.Write($"не удалось снять запрет DNS в локальной сети: {e.Message}"); }
    }

    // MARK: Канал

    private static async Task ServeLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipeWithRetry();
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                var accepted = server;
                server = null; // владение переходит обработчику
                _ = Task.Run(() => HandleConnectionSafeAsync(accepted), CancellationToken.None);

                // Тут же возвращаемся принимать следующего: приём не ждёт долгого
                // обработчика.
            }
            catch (OperationCanceledException)
            {
                // Служба остановлена. Это штатный выход, а не сбой: ловим отдельно
                // и до общего обработчика, иначе отмена попала бы в него, была бы
                // записана как ошибка — и цикл пошёл бы на второй круг.
                break;
            }
            catch (Exception e)
            {
                // Ни одна ошибка канала — обрыв клиента, отказ при создании — не
                // должна нас ронять: упавшую службу поднимут действия при падении,
                // но это перезапуск процесса, а не продолжение работы.
                Log.Write($"ошибка цикла канала: {e.Message}");

                try
                {
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                server?.Dispose();
            }
        }

        Log.Write("цикл канала завершён");
    }

    private static async Task HandleConnectionSafeAsync(NamedPipeServerStream server)
    {
        try
        {
            await HandleConnectionAsync(server).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Write($"ошибка соединения: {e.Message}");
        }
        finally
        {
            server.Dispose();
        }
    }

    private static async Task HandleConnectionAsync(NamedPipeServerStream server)
    {
        using var reader = new StreamReader(server);
        await using var writer = new StreamWriter(server) { AutoFlush = true };

        // Молчаливый клиент — зависший интерфейс, обрыв на полпути — не должен
        // занимать соединение навсегда.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        string? line;
        try
        {
            line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Write("канал: команда не пришла за 5 с, разрываю соединение");
            return;
        }

        if (string.IsNullOrEmpty(line)) return;

        Ipc.Request request;
        try
        {
            request = JsonSerializer.Deserialize<Ipc.Request>(line) ?? new Ipc.Request();
        }
        catch (JsonException e)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new Ipc.Response { Ok = false, Error = $"команду не разобрать: {e.Message}" })).ConfigureAwait(false);
            return;
        }

        // Команды, меняющие состояние туннеля, записываем вместе с тем, кто их
        // прислал: мы работаем под SYSTEM, и без такой записи злоупотребление
        // доступом к каналу не оставляло бы следов. Саму конфигурацию не пишем
        // никогда — в ней пароли.
        if (request.Cmd is "start" or "stop" or "reconnect")
            Log.Write($"команда «{request.Cmd}» от пользователя {ClientUser(server)}");

        var response = await HandleAsync(request).ConfigureAwait(false);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
    }

    private static string ClientUser(NamedPipeServerStream server)
    {
        try { return server.GetImpersonationUserName(); }
        catch { return "не определён"; }
    }

    private static NamedPipeServerStream CreatePipeWithRetry()
    {
        const int maxAttempts = 40;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return CreatePipe();
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // «Все копии канала заняты» и прочие временные отказы. Ловим по типу,
                // а не по тексту: на другой локали текст не совпадёт.
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException)
            {
                // Не удалось выставить права — работаем на умолчаниях, но говорим
                // громко: обычный пользователь может не достучаться до канала,
                // созданного SYSTEM.
                Log.Write("ВНИМАНИЕ: канал создан без прав доступа — интерфейс может не подключиться");
                return new NamedPipeServerStream(
                    Ipc.PipeName, PipeDirection.InOut, MaxPipeInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
        }

        return CreatePipe();
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        // Доступ даём интерактивным пользователям, а не всем прошедшим проверку
        // подлинности. Мы работаем под SYSTEM и выполняем привилегированные
        // операции, поэтому круг тех, кто может командовать, должен быть
        // минимальным. «Прошедшие проверку подлинности» — это в том числе
        // служебные и сетевые входы, которым здесь делать нечего. Интерактивная
        // группа есть в токене обычного сеанса, в том числе по удалённому
        // рабочему столу, и отсутствует у служб.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            Ipc.PipeName, PipeDirection.InOut, MaxPipeInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    // MARK: Команды

    private static async Task<Ipc.Response> HandleAsync(Ipc.Request request) => request.Cmd switch
    {
        "version" => new Ipc.Response { Ok = true, Version = HostVersion },
        // Без ворот: снимок состояния. Долгий запуск держит ворота, но опрос
        // обязан отвечать.
        "status" => new Ipc.Response { Ok = true, Running = IsRunningSnapshot() },
        "start" => await StartAsync(request.Config, request.KillSwitch, request.BlockLanDns).ConfigureAwait(false),
        "stop" => await StopAsync().ConfigureAwait(false),
        "reconnect" => await ReconnectAsync().ConfigureAwait(false),
        _ => new Ipc.Response { Ok = false, Error = $"неизвестная команда: {request.Cmd}" },
    };

    private static string HostVersion =>
        typeof(TunnelHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static async Task<Ipc.Response> StartAsync(string? config, bool killSwitch, bool blockLanDns)
    {
        if (string.IsNullOrWhiteSpace(config))
            return new Ipc.Response { Ok = false, Error = "пустая конфигурация" };

        // Проверяем до ворот: чужой долгий запуск не должен задерживать отказ по
        // заведомо негодной конфигурации.
        var validation = await EngineValidator.ValidateAsync(config, EnginePath).ConfigureAwait(false);
        if (!validation.Ok)
            return new Ipc.Response { Ok = false, Error = validation.Error ?? "конфигурация не прошла проверку" };

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _killSwitch = killSwitch;
            _blockLanDns = blockLanDns;

            _intentionalStop = true;
            await StopEngineAsync().ConfigureAwait(false);
            _intentionalStop = false;

            await TunLifecycle.PrepareForStartAsync(Log.Write).ConfigureAwait(false);

            await File.WriteAllTextAsync(ConfigPath, config).ConfigureAwait(false);

            // В конфигурации пароли и ключи. Читать её обычному пользователю
            // незачем: интерфейс собирает её сам и передаёт по каналу.
            var restrictProblem = SecureDirectory.RestrictFile(ConfigPath);
            if (restrictProblem is not null)
                Log.Write($"ВНИМАНИЕ: конфигурация доступна на чтение посторонним — {restrictProblem}");

            _lastConfig = config;

            return await StartAndVerifyAsync(allowTunRetry: true).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new Ipc.Response { Ok = false, Error = e.Message };
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Ipc.Response> ReconnectAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(_lastConfig))
                return new Ipc.Response { Ok = false, Error = "нет сохранённой конфигурации" };

            _intentionalStop = true;
            await StopEngineAsync().ConfigureAwait(false);
            _intentionalStop = false;

            await TunLifecycle.PrepareForStartAsync(Log.Write).ConfigureAwait(false);

            return await StartAndVerifyAsync(allowTunRetry: true).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new Ipc.Response { Ok = false, Error = e.Message };
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Запустить и убедиться, что движок действительно живёт. Вызывается под воротами.
    /// </summary>
    private static async Task<Ipc.Response> StartAndVerifyAsync(bool allowTunRetry)
    {
        var attempts = allowTunRetry ? 2 : 1;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                Log.Write("конфликт адаптера, повторная уборка…");
                await TunLifecycle.PrepareForStartAsync(Log.Write).ConfigureAwait(false);
                await Task.Delay(2000).ConfigureAwait(false);
            }

            if (!StartEngineProcess())
                return await FailStartAsync("движок не запустился").ConfigureAwait(false);

            var conflict = false;

            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(300).ConfigureAwait(false);

                // Именно «процесс жив», а не «процесс не завершён». Запись вида
                // `is not { HasExited: true }` здесь недопустима: свойственный
                // шаблон не сопоставляется с null, поэтому отсутствие процесса
                // читалось бы как «всё хорошо».
                if (_engine is { HasExited: false }) continue;

                var error = ReadEngineError();
                var tunConflict = error.Contains("already exists", StringComparison.OrdinalIgnoreCase);

                if (tunConflict && attempt == 0 && allowTunRetry)
                {
                    _intentionalStop = true;
                    await StopEngineAsync().ConfigureAwait(false);
                    _intentionalStop = false;
                    conflict = true;
                    break;
                }

                return await FailStartAsync($"движок завершился: {error}").ConfigureAwait(false);
            }

            if (conflict) continue;

            // Проверка обязана быть «жив», а не «не завершён» — по той же причине,
            // что и выше. Ложный успех здесь означал бы «подключено» вообще без
            // процесса: kill switch включается, весь исходящий блокируется, а
            // туннеля нет — машина остаётся без сети.
            if (_engine is { HasExited: false })
            {
                BeginLogReaders();
                _restartCount = 0;
                _everConnected = true;

                var protectionWarning = ApplyProtection();

                SystemJournal.Info(SystemJournal.Id.TunnelUp,
                    protectionWarning is null
                        ? "Туннель поднят, трафик машины идёт через него."
                        : $"Туннель поднят, но часть защиты не применилась: {protectionWarning}");

                return new Ipc.Response { Ok = true, Running = true, Warning = protectionWarning };
            }
        }

        return await FailStartAsync("движок завершился: не удалось поднять адаптер").ConfigureAwait(false);
    }

    /// <summary>
    /// Неудачный запуск. Что делать с блокировкой — зависит от того, стоял ли
    /// туннель до этого, и это разные положения, которые нельзя смешивать.
    ///
    /// <para><b>Туннель ни разу не встал.</b> Человек просто не смог подключиться:
    /// занят порт, испорчена конфигурация, движок в карантине. Оставить его при
    /// этом без сети абсурдно — защищать нечего, а машина станет нерабочей.
    /// Блокировку снимаем, конфигурацию забываем.</para>
    ///
    /// <para><b>Туннель стоял и умер.</b> Это ровно то событие, ради которого
    /// kill switch и включают. Снять блокировку здесь — значит выключить защиту
    /// в момент, когда она понадобилась. Блокировка остаётся, конфигурация
    /// остаётся, надзор продолжает попытки, а человеку говорится прямо: трафик
    /// закрыт, вернуть прямой доступ — «Отключить».</para>
    ///
    /// <para>Навсегда без сети машина всё равно не останется: блокировку снимут
    /// отключение в интерфейсе, перезапуск привилегированной части (она видит
    /// свою отметку и снимает) и аварийный <c>--firewall-reset</c>.</para>
    /// </summary>
    private static async Task<Ipc.Response> FailStartAsync(string error)
    {
        Log.Write($"неудачный запуск движка: {error}");

        var protectionHolds = _everConnected && _killSwitch;

        _intentionalStop = true;
        await StopEngineAsync().ConfigureAwait(false);
        _intentionalStop = false;

        if (protectionHolds)
        {
            Log.Write("блокировка остаётся: туннель стоял и упал — это и есть kill switch. " +
                      "Надзор продолжает попытки");

            SystemJournal.Warn(SystemJournal.Id.TrafficBlocked,
                $"Туннель упал ({error}), а Kill Switch включён — трафик заблокирован намеренно. " +
                "Клиент продолжает поднимать туннель. Вернуть прямой доступ: «Отключить» в окне Method.");

            // Конфигурацию не забываем: без неё надзору нечего поднимать.
            _ = Task.Run(SuperviseRestartAsync);

            return new Ipc.Response
            {
                Ok = false,
                Error = error,
                Warning = "Трафик заблокирован: туннель упал, а Kill Switch включён. " +
                          "Клиент продолжает поднимать туннель. Чтобы вернуть прямой доступ — «Отключить».",
            };
        }

        _lastConfig = null;

        try
        {
            KillSwitch.Disable();
            KillSwitch.UnblockIpv6();
            KillSwitch.UnblockLanDns();
            Log.Write("блокировка снята: туннель не поднимался, защищать нечего");
        }
        catch (Exception e)
        {
            // Молчать нельзя: если снять не удалось, машина осталась без сети, и это
            // единственный след, по которому можно будет понять причину.
            Log.Write($"КРИТИЧНО: не удалось снять блокировку: {e.Message}. " +
                      "Восстановление: Method.exe --firewall-reset от администратора");

            SystemJournal.Error(SystemJournal.Id.BlockStuck,
                $"Туннель не поднялся, а блокировку исходящего трафика снять не удалось: {e.Message}. " +
                "Пока она стоит, машина без сети. Восстановление: Method.exe --firewall-reset от администратора.");
        }

        return new Ipc.Response { Ok = false, Error = error };
    }

    /// <summary>
    /// Туннель в этой сессии хоть раз поднимался. Отличает «не смог подключиться»
    /// от «работал и упал» — а это два разных решения про блокировку.
    /// </summary>
    private static bool _everConnected;

    /// <summary>
    /// Защита, которая ставится после успешного подъёма туннеля. Возвращает текст
    /// предупреждения или пусто.
    ///
    /// <para>Результат обязателен к прочтению. Игнорировать его — худший из
    /// возможных исходов: интерфейс рисует бейдж «kill switch активен», защиты нет
    /// вообще, и узнать об этом неоткуда.</para>
    /// </summary>
    private static string? ApplyProtection()
    {
        var problems = new List<string>();

        if (_killSwitch && !KillSwitch.Enable(EnginePath, TunLifecycle.InterfaceName))
        {
            var why = KillSwitch.LastError ?? "причина неизвестна";
            Log.Write($"kill switch не включён: {why}");

            // Приводим состояние к правде: тумблер включён, а защиты нет. Иначе
            // остановка потом «снимала» бы несуществующую блокировку, а бейдж врал.
            _killSwitch = false;
            problems.Add($"Kill switch включить не удалось ({why}). " +
                         "Туннель работает, но если движок упадёт, трафик пойдёт напрямую.");
        }

        if (!KillSwitch.BlockIpv6())
        {
            Log.Write($"IPv6 не заблокирован: {KillSwitch.LastError}");
            problems.Add("Не удалось заблокировать IPv6. На сетях с IPv6 часть трафика может пойти мимо туннеля.");
        }

        if (_blockLanDns && !KillSwitch.BlockLanDns())
        {
            Log.Write($"запрет DNS в локальной сети не применён: {KillSwitch.LastError}");
            _blockLanDns = false;
            problems.Add("Запретить DNS в локальной сети не удалось — имена могут разрешаться мимо туннеля.");
        }
        else if (!_blockLanDns)
        {
            // Настройку могли выключить на живом туннеле: правила надо снять, иначе
            // внутренние имена локальной сети останутся закрытыми до перезапуска.
            KillSwitch.UnblockLanDns();
        }

        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private static async Task<Ipc.Response> StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _intentionalStop = true;
            await StopEngineAsync().ConfigureAwait(false);
            _lastConfig = null;
            _killSwitch = false;
            _everConnected = false;
            _blockLanDns = false;
        }
        finally
        {
            Gate.Release();
        }

        // Явная остановка — снимаем блокировки и возвращаем прямой интернет.
        // Результат обязателен к прочтению: человек нажал «отключить», увидел
        // «трафик идёт напрямую», а исходящий остался заблокированным — интернета
        // нет, и причина не названа нигде.
        var killSwitchOff = KillSwitch.Disable();
        KillSwitch.UnblockIpv6();
        KillSwitch.UnblockLanDns();

        if (killSwitchOff)
        {
            SystemJournal.Info(SystemJournal.Id.TunnelDown, "Туннель остановлен, трафик идёт напрямую.");
            return new Ipc.Response { Ok = true, Running = false };
        }

        var why = KillSwitch.LastError ?? "причина неизвестна";
        Log.Write($"kill switch не снят при остановке: {why}");

        SystemJournal.Error(SystemJournal.Id.BlockStuck,
            $"Туннель остановлен, но блокировку исходящего трафика снять не удалось: {why}. " +
            "Пока она стоит, машина без сети. Восстановление: Method.exe --firewall-reset от администратора.");

        return new Ipc.Response
        {
            Ok = true,
            Running = false,
            Warning = "Туннель остановлен, но снять блокировку исходящего трафика не удалось " +
                      $"({why}). Интернета может не быть. Восстановление: запустите " +
                      "«Method.exe --firewall-reset» от администратора.",
        };
    }

    // MARK: Процесс движка

    private static bool StartEngineProcess()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(EnginePath, ["run", "-c", ConfigPath])
                {
                    WorkingDirectory = WorkDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
                EnableRaisingEvents = true,
            };

            process.Exited += OnEngineExited;
            // Собственные записи движка — соединения, DNS, ошибки адаптера и
            // маршрутов — уходят в наш журнал. Без них при «подключено, а трафика
            // нет» причина не видна: процесс жив, но что-то не пересылает.
            process.OutputDataReceived += OnEngineOutput;
            process.ErrorDataReceived += OnEngineOutput;

            _engine = process;

            if (!process.Start())
            {
                process.Dispose();
                _engine = null;
                return false;
            }

            _lastStartUtc = DateTime.UtcNow;

            // Привязка к заданию сразу после старта: если мы умрём нештатно, Windows
            // погасит и движок. Неудача не отменяет запуск, но уходит в журнал —
            // иначе потеря страховки прошла бы незаметно.
            ProcessJobGuard.TryAssign(process, Log.Write);

            Log.Write($"движок запущен (pid {process.Id})");
            return true;
        }
        catch (Exception e)
        {
            // Запуск бросает, если файла нет или его держит антивирус. Не даём этому
            // уронить нас — вызывающий обработает отказ.
            Log.Write($"движок не запустился: {e.Message}");
            try { _engine?.Dispose(); } catch { /* уже освобождён */ }
            _engine = null;
            return false;
        }
    }

    private static void OnEngineOutput(object? sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data)) Log.WriteEngine(e.Data);
    }

    private static void BeginLogReaders()
    {
        var process = _engine;
        if (process is null) return;

        try
        {
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }
        catch (InvalidOperationException)
        {
            // Чтение уже начато — ничего страшного.
        }
    }

    private static void OnEngineExited(object? sender, EventArgs e)
    {
        try
        {
            var process = sender as Process ?? _engine;

            var code = 0;
            try { if (process is not null) code = process.ExitCode; } catch { /* уже освобождён */ }

            // Счётчик пауз сбрасывается здесь и только по факту стабильной работы:
            // процесс прожил дольше порога — значит падение изолированное. Если
            // сбрасывать его по времени с последнего успешного старта, то при
            // движке, который не запускается вовсе, отметка не обновляется, порог
            // истекает по настенным часам, счётчик обнуляется — и пауза вечно
            // откатывается к секунде. Получается бесконечный цикл перезапусков.
            var lived = DateTime.UtcNow - _lastStartUtc;
            if (lived > StableRunThreshold) _restartCount = 0;

            Log.Write($"движок завершился (код {code}, проработал {lived.TotalSeconds:N0} с)");

            var shouldRestart = !_intentionalStop && !string.IsNullOrEmpty(_lastConfig);

            // В системный журнал — только неожиданная смерть. Штатная остановка
            // туда уже записана как «туннель остановлен», и дублировать её значит
            // приучать читателя пролистывать.
            if (shouldRestart)
                SystemJournal.Warn(SystemJournal.Id.EngineDied,
                    $"Движок туннеля завершился сам (код {code}, проработал {lived.TotalSeconds:N0} с). " +
                    "Клиент поднимает его заново.");

            try { process?.Dispose(); } catch { /* уже освобождён */ }
            if (ReferenceEquals(_engine, process)) _engine = null;

            if (shouldRestart) _ = Task.Run(SuperviseRestartAsync);
        }
        catch (Exception ex)
        {
            // Необработанное исключение из обработчика завершения убивает процесс
            // целиком; поднимет нас система по действиям при падении, но это перезапуск с нуля.
            Log.Write($"ошибка в обработчике завершения: {ex.Message}");
        }
    }

    /// <summary>
    /// Надзор: поднять упавший движок с нарастающей паузой. Без паузы и без уборки
    /// адаптера это давало плотный цикл «адаптер уже существует» на полной загрузке
    /// процессора.
    /// </summary>
    private static async Task SuperviseRestartAsync()
    {
        try
        {
            var attempt = Interlocked.Increment(ref _restartCount);

            // Секунда, две, четыре, восемь, пятнадцать — а дальше редкие попытки раз
            // в минуту. Затяжной отказ (движок в карантине, нет прав, файл повреждён)
            // сам не рассосётся, и долбиться в него каждые пятнадцать секунд —
            // бессмысленная нагрузка. Но и прекращать нельзя: причина может уйти.
            var delayMs = attempt <= 5
                ? Math.Min(1000 * (1 << (attempt - 1)), MaxRestartBackoffMs)
                : LongRestartBackoffMs;

            await Task.Delay(delayMs).ConfigureAwait(false);

            var needRetry = false;

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_intentionalStop || string.IsNullOrEmpty(_lastConfig)) return;
                if (_engine is { HasExited: false }) return;

                Log.Write($"перезапуск движка (попытка {attempt}, пауза {delayMs} мс)…");
                await TunLifecycle.PrepareForStartAsync(Log.Write).ConfigureAwait(false);

                if (StartEngineProcess()) BeginLogReaders();
                else needRetry = true;
            }
            finally
            {
                Gate.Release();
            }

            if (needRetry) _ = Task.Run(SuperviseRestartAsync);
        }
        catch (Exception e)
        {
            Log.Write($"ошибка надзора: {e.Message}");
            // Не оставляем надзор мёртвым: планируем ещё одну попытку.
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000).ConfigureAwait(false);
                await SuperviseRestartAsync().ConfigureAwait(false);
            });
        }
    }

    /// <summary>Остановить движок. Вызывается под воротами.</summary>
    private static async Task StopEngineAsync()
    {
        var process = _engine;
        _engine = null;

        if (process is not null)
        {
            try
            {
                process.Exited -= OnEngineExited;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception e)
            {
                Log.Write($"остановка движка: {e.Message}");
            }
            finally
            {
                try { process.Dispose(); } catch { /* уже освобождён */ }
            }
        }

        await TunLifecycle.AfterStopAsync(Log.Write).ConfigureAwait(false);
    }

    /// <summary>Снимок состояния без ожидания ворот — одно чтение volatile-ссылки.</summary>
    private static bool IsRunningSnapshot()
    {
        var process = _engine;
        if (process is null) return false;

        try { return !process.HasExited; }
        catch { return false; }
    }

    /// <summary>
    /// Почему движок не поднялся.
    ///
    /// <para>Читаем оба потока, а не только поток ошибок: sing-box пишет свой
    /// журнал в обычный вывод, и причина падения — «порт занят», «адаптер
    /// существует», «конфигурация не разобрана» — оказывается именно там. Пока
    /// смотрели один поток ошибок, наружу выходило «причина неизвестна» на любую
    /// беду, и человек оставался с этим один на один.</para>
    ///
    /// <para>Читать так можно только до <c>BeginOutputReadLine</c>, то есть ровно
    /// на пути неудачного запуска. После успешного старта потоки разбирают
    /// обработчики.</para>
    /// </summary>
    private static string ReadEngineError()
    {
        var process = _engine;
        if (process is null) return "причина неизвестна";

        var said = new List<string>(2);

        foreach (var reader in new[] { process.StandardError, process.StandardOutput })
        {
            try
            {
                var text = reader.ReadToEnd().Trim();
                if (text.Length > 0) said.Add(text);
            }
            catch
            {
                // Поток уже разбирают обработчики или процесс освобождён —
                // спрашиваем второй.
            }
        }

        return said.Count == 0 ? "причина неизвестна" : Shorten(string.Join(" ", said));
    }

    /// <summary>
    /// Оставить от вывода движка суть. Он многословен, а в сообщение об ошибке
    /// идёт последняя строка — падение всегда объясняется в ней, и она же короче
    /// всего остального.
    /// </summary>
    private static string Shorten(string text)
    {
        var last = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.Length > 0);

        if (string.IsNullOrEmpty(last)) return "причина неизвестна";

        return last.Length <= 300 ? last : last[..300] + "…";
    }
}
