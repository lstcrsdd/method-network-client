// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using System.Runtime.Versioning;
using Method.Core;
using Method.Core.Tunnel;

namespace Method.App.Services;

/// <summary>
/// Установка привилегированной части.
///
/// <para>Она работает под SYSTEM, а запускает её служба Windows
/// <c>MethodTunnel</c> — см. <see cref="ServiceControl"/>. Начинали с
/// запланированной задачи, и она работала, но платой были три вещи: у задачи
/// нельзя спросить состояние (её «запущена» означает «действие выполняется»),
/// её нельзя корректно остановить (снятие — это убийство процесса, а убитый
/// держатель туннеля оставляет блокировку исходящего трафика), и она не
/// существует на сборках Windows с выключенным планировщиком.</para>
///
/// <para>Служба запускает <c>Method.exe --service</c> напрямую, без
/// промежуточного файла-запускателя. Промежуточный файл — это ещё один путь,
/// который надо защищать от подмены: подменивший его получил бы выполнение
/// своего кода от SYSTEM при следующей загрузке.</para>
///
/// <para>Установка поверх прежней версии снимает задачу, если та осталась от
/// сборки до перехода на службу. Иначе на машине оказались бы двое желающих
/// держать один и тот же канал.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    /// <summary>Задача из сборок до перехода на службу. Осталась только чтобы её снимать.</summary>
    private const string LegacyTaskName = "MethodTunnelHost";

    private const string FirewallRuleName = "Method Core";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppPaths.ProductName);

    public static string InstalledExe => Path.Combine(InstallDir, "Method.exe");

    private static readonly HelperLog Log = new(Path.Combine(AppPaths.ProgramDataDir, "install.log"));

    /// <summary>Журнал привилегированной части — на него ссылаются сообщения об отказах.</summary>
    private static string HelperLogPath => Path.Combine(AppPaths.ProgramDataDir, "helper.log");

    /// <summary>
    /// Служба зарегистрирована?
    ///
    /// <para>Вспомогательная проверка: главный признак — ответ самой
    /// привилегированной части по каналу. Здесь же мы всего лишь отличаем
    /// «поставлено, но молчит» от «не поставлено вовсе», чтобы человеку было что
    /// нажать.</para>
    /// </summary>
    public static bool HostRegistered() =>
        ServiceControl.Query() is not ServiceControl.State.Missing || LegacyTaskExists();

    /// <summary>Осталась ли задача от сборок до перехода на службу.</summary>
    private static bool LegacyTaskExists() => Run("schtasks", $"/Query /TN \"{LegacyTaskName}\"") == 0;

    /// <summary>
    /// Снять задачу из прежних сборок.
    ///
    /// <para>Делается при любой установке и удалении, даже когда задачи нет:
    /// команда на несуществующей задаче просто вернёт ошибку, а вот оставленная
    /// задача подняла бы вторую привилегированную часть, и две штуки принялись бы
    /// делить один канал и один адаптер.</para>
    /// </summary>
    private static void RemoveLegacyTask()
    {
        if (!LegacyTaskExists()) return;

        Run("schtasks", $"/End /TN \"{LegacyTaskName}\"");
        var code = Run("schtasks", $"/Delete /TN \"{LegacyTaskName}\" /F");
        Log.Write($"задача прежних сборок снята (код {code})");
    }

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

            // Службу останавливаем по-хорошему, а не убийством процесса. У
            // остановленной по-хорошему не сработают действия при падении — иначе
            // система подняла бы её обратно ровно в тот момент, когда мы
            // переписываем её же файлы.
            var stopProblem = ServiceControl.Stop(TimeSpan.FromSeconds(30));
            if (stopProblem is not null) Log.Write($"остановка службы: {stopProblem}");

            // Выключаем до убийства процессов: убитая служба для системы —
            // упавшая, и она поднимет её обратно через пять секунд, пока мы
            // переписываем её файлы. Обратно включит регистрация.
            ServiceControl.SetStartType(enabled: false, Log.Write);

            RemoveLegacyTask();
            StopHost();
            CopyApplication();
            ExtractEngine();

            var registerProblem = ServiceControl.Register(InstalledExe, Log.Write);
            if (registerProblem is not null) return FailInstall(registerProblem);

            // Источник в системном журнале регистрируется здесь, а не при первом
            // событии: регистрация требует прав администратора, а привилегированная
            // часть хоть их и имеет, но писать в реестр на горячем пути — плохая
            // привычка. Без источника события просто не попадут в журнал, туннель
            // от этого не пострадает.
            SystemJournal.EnsureSource(Log.Write);

            ConfigureFirewall();

            var startProblem = ServiceControl.Start(TimeSpan.FromSeconds(30));
            if (startProblem is not null) return FailInstall($"{startProblem} — смотри {HelperLogPath}");

            // Проверяем результат, а не факт запуска. Служба может отрапортовать
            // «выполняется» и тут же свернуться — например, если рядом с
            // исполняемым файлом не оказалось того, что ему нужно. Сказать
            // «готово», не спросив, значит соврать: человек нажмёт «Подключить» и
            // получит отказ без объяснения.
            if (!WaitForHost(TimeSpan.FromSeconds(20)))
                return FailInstall($"служба зарегистрирована, но привилегированная часть не отвечает — смотри {HelperLogPath}");

            Log.Write("установка закончена, привилегированная часть отвечает");
            return new InstallOutcome(true, null);
        }
        catch (Exception e)
        {
            return FailInstall($"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Отказ установки. От обычного отличается тем, что возвращает службе запуск
    /// при загрузке: установка его снимала, и уйти, не вернув, значило бы оставить
    /// после неудачной попытки заведомо мёртвую запись — та бы молчала и после
    /// перезагрузки.
    /// </summary>
    private static InstallOutcome FailInstall(string reason)
    {
        ServiceControl.SetStartType(enabled: true, Log.Write);
        return Fail(reason);
    }

    public static InstallOutcome Uninstall()
    {
        if (!Elevation.IsElevated())
            return new InstallOutcome(false, "удаление требует прав администратора");

        try
        {
            Log.Write("удаление началось");

            // Сначала останавливаем, потом снимаем запись. В обратном порядке
            // служба осталась бы работать до перезагрузки — с поднятым туннелем и
            // без всякой возможности ею командовать.
            var stopProblem = ServiceControl.Stop(TimeSpan.FromSeconds(30));
            if (stopProblem is not null) Log.Write($"остановка службы: {stopProblem}");

            // Отказ здесь — это отказ удаления целиком. Снести файлы, оставив
            // запись службы, значит получить службу, которая при каждой загрузке
            // пытается запустить несуществующий файл и пишет об этом в системный
            // журнал, а починить её нечем: клиента на машине уже нет.
            var removeProblem = ServiceControl.Remove(Log.Write);
            if (removeProblem is not null) return Fail(removeProblem);

            RemoveLegacyTask();
            SystemJournal.RemoveSource(Log.Write);
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
    /// установки не стартует вовсе — служба поднимает его, он умирает мгновенно, и
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
            // служба снята, правила сняты, туннель больше не поднимется.
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
