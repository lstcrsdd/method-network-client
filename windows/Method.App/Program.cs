// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Method.App.Services;

namespace Method.App;

/// <summary>
/// Точка входа. Один файл Method.exe работает в нескольких режимах, и режим
/// выбирается аргументом: так на машине человека нет второго исполняемого файла,
/// который можно потерять при копировании или подменить по отдельности.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var mode = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal));

        return mode switch
        {
            null => RunGui(args),
            "--service" => RunHost(args),
            "--firewall-reset" => ResetFirewall(),
            "--wfp-check" => CheckFilters(),
            "--install" => RunInstaller(install: true),
            "--uninstall" => RunInstaller(install: false),
            _ => UnknownMode(mode),
        };
    }

    private static int RunGui(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Привилегированный режим.
    ///
    /// <para>Обычно сюда попадает служба Windows: процесс отдаётся диспетчеру, и
    /// возврат случится, когда службу остановят. Отдельный ключ <c>--debug</c>
    /// запускает ту же привилегированную часть прямо в консоли, где остановка —
    /// это Ctrl+C. Различать эти два случая самим, по догадкам о том, кто нас
    /// запустил, нельзя: догадка «мы под диспетчером» ошибается ровно в ту
    /// сторону, где служба зависает на запуске.</para>
    ///
    /// <para>Консоль подключается до запуска: если журнал окажется недоступен,
    /// объяснение уйдёт хотя бы туда, откуда режим запустили. Под службой консоли
    /// нет, и вызов просто ничего не делает.</para>
    /// </summary>
    private static int RunHost(string[] args)
    {
        AttachToParentConsole();

        if (!args.Contains("--debug", StringComparer.OrdinalIgnoreCase))
        {
            TunnelService.RunUnderScm();
            return 0;
        }

        using var stopping = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            // Отменяем стандартную реакцию: она убивает процесс на месте, а нам
            // надо погасить движок и снять блокировку.
            e.Cancel = true;
            WriteLine("Method: останавливаюсь…");
            stopping.Cancel();
        };

        return TunnelHost.RunAsync(stopping.Token).GetAwaiter().GetResult();
    }

    /// <summary>Установка и удаление привилегированной части. Требуют прав администратора.</summary>
    private static int RunInstaller(bool install)
    {
        AttachToParentConsole();

        var outcome = install ? Installer.Install() : Installer.Uninstall();

        Console.Error.WriteLine(outcome.Ok
            ? "Method: готово."
            : $"Method: не вышло — {outcome.Reason}");

        return outcome.Ok ? 0 : 1;
    }

    // Вызывается сборщиком превью Avalonia, поэтому public и с этим именно именем.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Аварийный выход: снять блокировку исходящего трафика, если она пережила
    /// падение и машина осталась без сети. Именно этот режим называют сообщения об
    /// ошибках, поэтому он обязан работать сам по себе — без интерфейса и без
    /// привилегированной части.
    /// </summary>
    private static int ResetFirewall()
    {
        if (!Elevation.IsElevated())
        {
            WriteLine("Method: сброс правил брандмауэра требует прав администратора. " +
                      "Запустите командную строку от имени администратора и повторите.");
            return 77;
        }

        var ok = KillSwitch.Disable(force: true);
        KillSwitch.UnblockIpv6();
        KillSwitch.UnblockLanDns();

        if (ok)
        {
            WriteLine("Method: блокировка снята, исходящий трафик разрешён.");
            return 0;
        }

        WriteLine($"Method: снять блокировку не удалось ({KillSwitch.LastError ?? "причина неизвестна"}).");
        return 1;
    }

    /// <summary>
    /// Проверить, что блокировка на фильтрах платформы на этой машине ставится,
    /// действительно режет трафик и снимается.
    ///
    /// <para>Проверка нужна отдельно от подключения: фильтры может отвергнуть
    /// стороннее средство защиты, и узнать об этом в момент, когда человек жмёт
    /// «подключить», означало бы отказ вместо туннеля.</para>
    ///
    /// <para>Проверяется не установка фильтров, а результат. Поставленный фильтр
    /// — это ещё не блокировка: он мог оказаться перекрыт чужим разрешением с
    /// большим весом, привязан не к тому интерфейсу или просто не подойти ни к
    /// одному соединению. Разница видна только пробой, поэтому проба и делается
    /// трижды: до, во время и после.</para>
    /// </summary>
    private static int CheckFilters()
    {
        if (!Elevation.IsElevated())
        {
            WriteLine("Method: проверка фильтров требует прав администратора. " +
                      "Запустите командную строку от имени администратора и повторите.");
            return 77;
        }

        // Платформа фильтрации живёт в службе «Базовая фильтрация». Без неё
        // фильтры не поставить вовсе, и отказ выглядел бы кодом ошибки без
        // объяснения.
        var filteringProblem = FilteringEngineProblem();
        if (filteringProblem is not null)
        {
            WriteLine($"Method: {filteringProblem}");
            return 1;
        }

        var engine = Method.Core.AppPaths.EngineFile;
        WriteLine($"Method: движок — {engine}");

        if (!Probe(out var why))
        {
            WriteLine($"Method: сеть не отвечает ещё ДО блокировки ({why}). " +
                      "Проверять нечего: отличить нашу блокировку от общего отсутствия сети будет нельзя.");
            return 1;
        }

        WriteLine("Method: сеть до блокировки есть. Ставлю блокировку…");

        using var guard = new WfpKillSwitch();

        if (!guard.Enable(engine, Method.Core.Tunnel.TunLifecycle.InterfaceName, allowLan: true))
        {
            WriteLine($"Method: блокировка НЕ поставлена — {guard.LastError ?? "причина неизвестна"}");
            return 1;
        }

        var leaks = Probe(out _);
        guard.Disable();

        var restored = Probe(out var afterWhy);

        if (leaks)
        {
            WriteLine("Method: ПЛОХО — фильтры встали, но трафик прошёл мимо них. " +
                      "Блокировку в таком виде включать нельзя: она обещает защиту, которой нет.");
        }
        else
        {
            WriteLine("Method: блокировка работает — при ней соединение не проходит.");
        }

        if (!restored)
        {
            // Сеть после снятия обязана вернуться. Если не вернулась — говорим
            // прямо и напоминаем, что динамический сеанс система снимает сама при
            // выходе процесса: то есть выход отсюда и есть лечение.
            WriteLine($"Method: ВНИМАНИЕ — после снятия сеть не вернулась ({afterWhy}). " +
                      "Фильтры динамического сеанса система снимет при выходе этого процесса; " +
                      "если сеть не вернётся и после — перезагрузка уберёт их гарантированно.");
            return 1;
        }

        WriteLine("Method: блокировка снята, сеть вернулась.");
        return leaks ? 1 : 0;
    }

    /// <summary>
    /// Работает ли платформа фильтрации. Возвращает описание беды или <c>null</c>.
    /// </summary>
    private static string? FilteringEngineProblem()
    {
        try
        {
            using var bfe = new System.ServiceProcess.ServiceController("BFE");

            return bfe.Status is System.ServiceProcess.ServiceControllerStatus.Running
                ? null
                : $"служба «Базовая фильтрация» (BFE) в состоянии {bfe.Status}. " +
                  "Пока она не запущена, блокировку утечки поставить нельзя ничем.";
        }
        catch (Exception e)
        {
            return $"состояние службы «Базовая фильтрация» (BFE) выяснить не удалось: {e.Message}";
        }
    }

    /// <summary>
    /// Проба сети: пройдёт ли исходящее соединение отсюда наружу.
    ///
    /// <para>Адреса заданы числами и берутся три штуки. Имя разрешать нельзя —
    /// при поднятой блокировке не пройдёт и запрос имени, и вместо «трафик
    /// закрыт» мы получили бы «имя не разрешилось», то есть тот же ответ по
    /// другой причине. Три адреса — чтобы отказ одного узла не сошёл за
    /// блокировку.</para>
    /// </summary>
    private static bool Probe(out string why)
    {
        (string Host, int Port)[] targets = [("1.1.1.1", 443), ("8.8.8.8", 443), ("9.9.9.9", 443)];

        var reasons = new List<string>();

        foreach (var (host, port) in targets)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();

                if (client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(3)))
                {
                    why = "";
                    return true;
                }

                reasons.Add($"{host}: не ответил за три секунды");
            }
            catch (Exception e)
            {
                reasons.Add($"{host}: {(e.InnerException ?? e).Message}");
            }
        }

        why = string.Join("; ", reasons);
        return false;
    }

    /// <summary>
    /// Режим объявлен, но не написан. Возвращаем ненулевой код и говорим прямо:
    /// установщик, который «отработал» и ничего не сделал, обнаруживается уже на
    /// машине человека.
    /// </summary>
    private static int NotImplemented(string what)
    {
        WriteLine($"Method: {what} ещё не реализована в этой сборке.");
        return 2;
    }

    private static int UnknownMode(string mode)
    {
        WriteLine($"Method: неизвестный режим «{mode}». Доступны: --service [--debug], --install, --uninstall, " +
                  "--firewall-reset, --wfp-check.");
        return 64;
    }

    /// <summary>
    /// Сборка помечена как WinExe и своей консоли не имеет. Без подключения к
    /// консоли вызывающего сообщения об ошибках уходили бы в никуда — а невидимая
    /// ошибка ничем не отличается от молчаливого успеха.
    /// </summary>
    private static void WriteLine(string message)
    {
        AttachToParentConsole();
        Console.Error.WriteLine(message);
    }

    /// <summary>
    /// Подключиться к консоли вызывающего и писать в той кодировке, которую эта
    /// консоль понимает.
    ///
    /// <para>Кодировку приходится задавать руками: среда выполнения решает, есть ли
    /// консоль, ещё при запуске — то есть до подключения, — и остаётся на UTF-8.
    /// В консоли с другой кодовой страницей русский текст превращается в мусор, а
    /// нечитаемое сообщение об ошибке немногим лучше отсутствующего.</para>
    /// </summary>
    private static void AttachToParentConsole()
    {
        if (_consoleAttached) return;
        _consoleAttached = true;

        if (!AttachConsole(AttachParentProcess)) return;

        try
        {
            var codePage = (int)GetConsoleOutputCP();
            if (codePage > 0) Console.OutputEncoding = Encoding.GetEncoding(codePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            // Кодировку задать не вышло — пишем как есть. Текст может оказаться
            // нечитаемым, но молчать хуже.
        }
    }

    private static bool _consoleAttached;

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();
}
