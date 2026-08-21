// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Method.Core;
using Method.Core.Tunnel;

namespace Method.App.Services;

/// <summary>
/// Установка привилегированной части.
///
/// <para>Она работает под SYSTEM, а запускает её запланированная задача. Не служба:
/// служба — это своя запись в базе служб, свой жизненный цикл и свой способ не
/// подняться после обновления. Задача даёт те же права и снимается тем же
/// движением, каким ставится.</para>
///
/// <para>Задача запускает <c>Method.exe --service</c> напрямую, без промежуточного
/// файла-запускателя. Промежуточный файл — это ещё один путь, который надо
/// защищать от подмены: подменивший его получил бы выполнение своего кода от
/// SYSTEM при ближайшем входе в систему.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    public const string TaskName = "MethodTunnelHost";

    private const string FirewallRuleName = "Method Core";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppPaths.ProductName);

    public static string InstalledExe => Path.Combine(InstallDir, "Method.exe");

    private static readonly HelperLog Log = new(Path.Combine(AppPaths.ProgramDataDir, "install.log"));

    /// <summary>
    /// Задача зарегистрирована?
    ///
    /// <para>Достоверен только положительный ответ: из-под обычного пользователя
    /// запрос к чужой задаче может не пройти по правам, и «нет» здесь означает
    /// «не смогли посмотреть» ровно так же, как «не установлено». Поэтому
    /// состояние в интерфейсе показывается по ответу самой привилегированной
    /// части, а это — вспомогательная проверка.</para>
    /// </summary>
    public static bool TaskExists() => Run("schtasks", $"/Query /TN \"{TaskName}\"") == 0;

    // MARK: Установка

    public static InstallOutcome Install()
    {
        if (!Elevation.IsElevated())
            return new InstallOutcome(false, "установка требует прав администратора");

        try
        {
            Log.Write("установка началась");

            Directory.CreateDirectory(AppPaths.ProgramDataDir);

            // Права урезаются до того, как в каталог что-то ляжет: каталог, из
            // которого SYSTEM запускает исполняемый файл, обязан быть недоступен
            // на запись обычному пользователю.
            var hardenProblem = SecureDirectory.Harden(AppPaths.ProgramDataDir);
            if (hardenProblem is not null) Log.Write($"права рабочего каталога: {hardenProblem}");

            StopHost();
            CopyApplication();
            ExtractEngine();

            if (!RegisterTask())
                return Fail("зарегистрировать запланированную задачу не удалось");

            ConfigureFirewall();

            Run("schtasks", $"/Run /TN \"{TaskName}\"");

            // Проверяем результат, а не факт запуска. Задача может отработать и
            // тут же умереть — например, если рядом с исполняемым файлом не
            // оказалось того, что ему нужно. Сказать «готово», не спросив, значит
            // соврать: человек нажмёт «Подключить» и получит отказ без объяснения.
            if (!WaitForHost(TimeSpan.FromSeconds(20)))
                return Fail("задача зарегистрирована, но привилегированная часть не отвечает — " +
                            $"смотри {Path.Combine(AppPaths.ProgramDataDir, "helper.log")}");

            Log.Write("установка закончена, привилегированная часть отвечает");
            return new InstallOutcome(true, null);
        }
        catch (Exception e)
        {
            return Fail($"{e.GetType().Name}: {e.Message}");
        }
    }

    public static InstallOutcome Uninstall()
    {
        if (!Elevation.IsElevated())
            return new InstallOutcome(false, "удаление требует прав администратора");

        try
        {
            Log.Write("удаление началось");

            Run("schtasks", $"/End /TN \"{TaskName}\"");
            Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");
            StopHost();

            // Правила брандмауэра снимаются обязательно и до удаления файлов:
            // оставленная блокировка исходящего трафика переживёт удаление клиента,
            // и машина останется без сети, а чинить будет уже нечем.
            KillSwitch.Disable();
            KillSwitch.UnblockIpv6();
            KillSwitch.UnblockLanDns();
            RemoveFirewallRules();

            TryDelete(AppPaths.ProgramDataDir);
            TryDelete(InstallDir);

            Log.Write("удаление закончено");
            return new InstallOutcome(true, null);
        }
        catch (Exception e)
        {
            return Fail($"{e.GetType().Name}: {e.Message}");
        }
    }

    private static InstallOutcome Fail(string reason)
    {
        Log.Write("НЕ ВЫШЛО: " + reason);
        return new InstallOutcome(false, reason);
    }

    // MARK: Шаги

    /// <summary>Погасить работающую привилегированную часть — иначе её файл занят.</summary>
    private static void StopHost()
    {
        foreach (var process in Process.GetProcessesByName("Method"))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;

                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* чужой процесс */ }

                if (path is null) continue;
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExe),
                        StringComparison.OrdinalIgnoreCase)) continue;

                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                Log.Write($"остановлен прежний процесс (pid {process.Id})");
            }
            catch (Exception e)
            {
                Log.Write($"процесс {process.Id} не остановлен: {e.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        // Движок мог остаться сиротой от прошлой сессии.
        TunLifecycle.KillOrphanEngine(Log.Write);
    }

    /// <summary>
    /// Переносит приложение целиком, а не один исполняемый файл.
    ///
    /// <para>Копировать только exe нельзя, и это стоило разбора: у сборки, собранной
    /// не в один файл, рядом лежат две сотни библиотек. Одинокий exe в каталоге
    /// установки не стартует вовсе — задача поднимает его, он умирает мгновенно, и
    /// журнала после себя не оставляет, потому что не успевает дойти до первой
    /// строки. Со стороны это выглядит как «установка прошла, но ничего не
    /// работает».</para>
    ///
    /// <para>Для сборки в один файл копирование каталога обходится тем же одним
    /// файлом, поэтому отдельного случая не нужно.</para>
    /// </summary>
    private static void CopyApplication()
    {
        var source = Environment.ProcessPath
                     ?? throw new InvalidOperationException("не удалось определить свой путь");

        var sourceDir = Path.GetDirectoryName(source)
                        ?? throw new InvalidOperationException("не удалось определить свой каталог");

        if (string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(InstallDir),
                StringComparison.OrdinalIgnoreCase))
        {
            Log.Write("запущены уже из каталога установки, копировать нечего");
            return;
        }

        Directory.CreateDirectory(InstallDir);
        var problem = SecureDirectory.Harden(InstallDir);
        if (problem is not null) Log.Write($"права каталога установки: {problem}");

        var count = CopyDirectory(sourceDir, InstallDir);
        Log.Write($"перенесено файлов: {count} → {InstallDir}");

        if (!File.Exists(InstalledExe))
            throw new FileNotFoundException($"после копирования нет {InstalledExe}");
    }

    private static int CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
            count++;
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
            count += CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));

        return count;
    }

    /// <summary>Дождаться, пока привилегированная часть отзовётся по каналу.</summary>
    private static bool WaitForHost(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (TunnelClient.IsAvailableAsync().GetAwaiter().GetResult()) return true;
            Thread.Sleep(500);
        }

        return false;
    }

    /// <summary>
    /// Разложить движок рядом. Привилегированная часть делает это и сама при
    /// запуске, но здесь мы ещё под правами администратора и можем сказать вслух,
    /// если движка не оказалось, — а она в этот момент уже никого не спросит.
    /// </summary>
    private static void ExtractEngine()
    {
        var installDir = Path.GetDirectoryName(InstalledExe) ?? InstallDir;

        if (!EmbeddedPayload.ExtractWithFallback("sing-box.exe", AppPaths.EngineFile,
                Path.Combine(installDir, "sing-box.exe")))
            throw new FileNotFoundException("движок sing-box.exe не найден ни в сборке, ни рядом с ней");

        if (!EmbeddedPayload.ExtractWithFallback("wintun.dll",
                Path.Combine(AppPaths.ProgramDataDir, "wintun.dll"),
                Path.Combine(installDir, "wintun.dll")))
            throw new FileNotFoundException("wintun.dll не найден ни в сборке, ни рядом с ней");

        Log.Write("движок разложен");
    }

    /// <summary>
    /// Задача описывается XML, а не набором ключей командной строки: перезапуск
    /// при падении и снятие предельного времени работы через ключи не задаются
    /// вовсе, а без них задача умирает через трое суток и не поднимается до
    /// перезагрузки.
    /// </summary>
    private static bool RegisterTask()
    {
        var xmlPath = Path.Combine(AppPaths.ProgramDataDir, "task.xml");

        var xml =
            """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Method: привилегированная часть туннеля</Description></RegistrationInfo>
              <Triggers>
                <BootTrigger><Enabled>true</Enabled><Delay>PT30S</Delay></BootTrigger>
                <LogonTrigger><Enabled>true</Enabled><Delay>PT15S</Delay></LogonTrigger>
              </Triggers>
              <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <RestartOnFailure><Interval>PT1M</Interval><Count>99</Count></RestartOnFailure>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
              </Settings>
              <Actions><Exec><Command>{COMMAND}</Command><Arguments>--service</Arguments></Exec></Actions>
            </Task>
            """
            .Replace("{COMMAND}", InstalledExe.Replace("&", "&amp;"), StringComparison.Ordinal);

        // Кодировка UTF-16 обязательна: планировщик читает XML по объявлению в
        // самом файле и на несовпадении отвергает его целиком.
        File.WriteAllText(xmlPath, xml, Encoding.Unicode);

        var code = RunWithOutput("schtasks", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", out var output);
        Log.Write($"регистрация задачи: код {code}, {output.Trim()}");

        return code == 0 && TaskExists();
    }

    /// <summary>
    /// Разрешить движку сеть. Без этого правила брандмауэр спрашивает разрешение
    /// у того, кого рядом нет: движок запускает SYSTEM, и окно с вопросом человек
    /// не увидит — соединение просто не встанет.
    /// </summary>
    private static void ConfigureFirewall()
    {
        RemoveFirewallRules();

        Run("netsh", $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=out action=allow " +
                     $"program=\"{AppPaths.EngineFile}\" enable=yes profile=any");
        Run("netsh", $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow " +
                     $"program=\"{AppPaths.EngineFile}\" enable=yes profile=any");

        Log.Write("правила брандмауэра для движка поставлены");
    }

    private static void RemoveFirewallRules() =>
        Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception e)
        {
            // Каталог мог быть занят. Это не повод считать удаление неудавшимся:
            // задача снята, правила сняты, туннель больше не поднимется.
            Log.Write($"каталог {path} не удалён: {e.Message}");
        }
    }

    // MARK: Запуск сторонних программ

    private static int Run(string file, string arguments) => RunWithOutput(file, arguments, out _);

    private static int RunWithOutput(string file, string arguments, out string output)
    {
        output = "";

        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return -1;

            // Вывод вычитывается до ожидания: переполнение буфера иначе подвешивает
            // дочерний процесс, а вместе с ним и установку.
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                output = "превышено время ожидания";
                return -1;
            }

            output = (stdout + stderr).Trim();
            return process.ExitCode;
        }
        catch (Exception e)
        {
            output = e.Message;
            return -1;
        }
    }
}

/// <param name="Ok">Получилось ли.</param>
/// <param name="Reason">Причина отказа. Пусто, когда получилось.</param>
public sealed record InstallOutcome(bool Ok, string? Reason);
