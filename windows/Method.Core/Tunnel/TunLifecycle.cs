// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace Method.Core.Tunnel;

/// <summary>
/// Уборка перед запуском движка и после остановки.
///
/// Без неё следующее подключение натыкается на обломки прошлого: «set ipv4
/// address: the object already exists», подвисание на десятки секунд и
/// «таймаут подключения» на ровном месте.
///
/// <para><b>Главное правило: трогаем только своё.</b> На машине человека
/// параллельно живут другие клиенты на том же драйвере. Погасить чужой процесс
/// или отключить чужой адаптер — значит оборвать чужой туннель, и выглядеть это
/// будет как поломка того клиента, а не нашего.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class TunLifecycle
{
    /// <summary>
    /// Имя сетевого адаптера. Wintun присваивает его уже после создания
    /// устройства, поэтому оно же — единственная зацепка, чтобы потом убрать
    /// за собой.
    /// </summary>
    public const string InterfaceName = AppPaths.ProductName;

    /// <summary>Вызывать перед каждым запуском и переподключением.</summary>
    public static async Task PrepareForStartAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        KillOrphanEngine(log);
        // Windows освобождает LUID и адрес не мгновенно; без пауз следующий старт
        // видит адаптер ещё занятым.
        await Task.Delay(800, ct).ConfigureAwait(false);
        await CleanUpStaleAdaptersAsync(log, ct).ConfigureAwait(false);
        await Task.Delay(700, ct).ConfigureAwait(false);
    }

    /// <summary>После остановки — дать драйверу освободить адрес.</summary>
    public static async Task AfterStopAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        KillOrphanEngine(log);
        await Task.Delay(500, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Гасит только наш движок — по совпадению пути исполняемого файла. Гасить
    /// по имени процесса нельзя: на машине бывает запущен sing-box другого
    /// клиента, и его туннель оборвётся вместе с нашим.
    /// </summary>
    public static void KillOrphanEngine(Action<string>? log = null)
    {
        string ours;
        try
        {
            ours = Path.GetFullPath(AppPaths.EngineFile);
        }
        catch (Exception e)
        {
            log?.Invoke($"Уборка: путь к движку не разобрать ({e.GetType().Name})");
            return;
        }

        foreach (var process in Process.GetProcessesByName("sing-box"))
        {
            try
            {
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // К модулю чужого процесса доступа может не быть — значит он не наш.
                }

                var isOurs = path is not null
                             && string.Equals(Path.GetFullPath(path), ours, StringComparison.OrdinalIgnoreCase);

                if (isOurs && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    log?.Invoke($"Уборка: погашен осиротевший движок (pid {process.Id})");
                }
            }
            catch (Exception e)
            {
                log?.Invoke($"Уборка: процесс {process.Id} не осмотреть ({e.GetType().Name})");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Убирает обломки прошлых запусков. Два разных случая и два разных лечения.
    ///
    /// <para><b>Наш адаптер остался включённым.</b> Отключаем по имени. Имя
    /// сверяется точно, а не по маске: маска «Method*» задела бы адаптеры чужих
    /// клиентов с похожим именем, а это ровно то, чего делать нельзя. Учтён и
    /// случай, когда имя оказалось занято и драйвер добавил к нему номер.</para>
    ///
    /// <para><b>Устройство осталось сломанным.</b> Если запуск упал на полпути,
    /// в системе остаётся устройство драйвера, которое нельзя ни поднять, ни
    /// отключить: статус «Unknown» — запись есть, устройства нет; «Error» —
    /// осталось после падения. Именно они и подвешивают следующий старт, и
    /// убираются только удалением. Такое устройство не принадлежит уже никому,
    /// поэтому убрать его — починка, а не вмешательство в чужой клиент.</para>
    ///
    /// <para>Заведомо работающие чужие адаптеры не трогаются вовсе. Раньше здесь
    /// отключался любой неподнятый адаптер драйвера — это чинило наш запуск ценой
    /// чужого выключённого туннеля, который потом не включался сам.</para>
    /// </summary>
    private static async Task CleanUpStaleAdaptersAsync(Action<string>? log, CancellationToken ct)
    {
        var script = string.Join("; ",
        [
            // 1. Наши адаптеры: точное имя либо имя с номером, который добавил драйвер.
            "$mine = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { " +
                $"$_.Name -eq '{InterfaceName}' -or $_.Name -match '^{InterfaceName} \\d+$' }}",
            "foreach ($a in $mine) { try { Disable-NetAdapter -Name $a.Name -Confirm:$false -ErrorAction SilentlyContinue } catch {} }",

            // 2. Сломанные устройства драйвера — только удалением.
            "$broken = Get-PnpDevice -Class Net -ErrorAction SilentlyContinue | Where-Object { " +
                "$_.FriendlyName -like '*Wintun*' -and $_.Status -in @('Unknown','Error') }",
            "foreach ($d in $broken) { try { & pnputil /remove-device $d.InstanceId } catch {} }",
        ]);

        await RunHiddenAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
            log,
            ct).ConfigureAwait(false);
    }

    private static async Task RunHiddenAsync(string file, string arguments, Action<string>? log, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                log?.Invoke($"Уборка: {file} не запустился");
                return;
            }

            // Вывод вычитывается до ожидания: переполнение буфера иначе подвешивает
            // дочерний процесс, а вместе с ним и того, кто его ждёт.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Не уложился — обязательно гасим. Брошенный на произвол скрипт
                // продолжает работать и отключает адаптер уже после того, как новый
                // туннель поднялся. Симптом — «подключено, трафика нет» без единой
                // ошибки в журнале.
                log?.Invoke($"Уборка: {file} не уложился в 8 с, гашу");
                try { process.Kill(entireProcessTree: true); } catch { /* уже умер */ }
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"Уборка: {file} не отработал ({e.GetType().Name}: {e.Message})");
        }
    }
}
