// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Method.App.Services;

namespace Method.App.ViewModels;

/// <summary>
/// Управление привилегированной частью из интерфейса.
///
/// <para>Само приложение работает под обычным пользователем и повышать себе права
/// не может и не должно. Установка запускает тот же файл с другим аргументом через
/// запрос прав — согласие даёт человек, и даёт его один раз.</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Состояние привилегированной части, как его видит интерфейс.</summary>
    public enum HostState
    {
        Unknown,
        Ready,
        NotInstalled,
        Installed,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostStatusText))]
    [NotifyPropertyChangedFor(nameof(CanInstallHost))]
    [NotifyPropertyChangedFor(nameof(CanRemoveHost))]
    private HostState _host = HostState.Unknown;

    [ObservableProperty]
    private bool _isHostBusy;

    public string HostStatusText => Host switch
    {
        HostState.Ready => "Установлена и отвечает",
        HostState.Installed => "Задача есть, но не отвечает",
        HostState.NotInstalled => "Не установлена",
        _ => "Проверяется…",
    };

    public bool CanInstallHost => Host is not HostState.Ready;

    public bool CanRemoveHost => Host is HostState.Ready or HostState.Installed;

    /// <summary>
    /// Проверка состояния. Сначала спрашиваем саму привилегированную часть: она
    /// отвечает по каналу, и положительный ответ означает, что всё действительно
    /// работает. Наличие задачи — только запасной признак: из-под обычного
    /// пользователя запрос к чужой задаче может не пройти по правам, и «нет» там
    /// означает «не смогли посмотреть» ровно так же, как «не установлено».
    /// </summary>
    [RelayCommand]
    public async Task CheckHostAsync()
    {
        if (await TunnelClient.IsAvailableAsync().ConfigureAwait(true))
        {
            Host = HostState.Ready;
            return;
        }

        Host = Installer.TaskExists() ? HostState.Installed : HostState.NotInstalled;
    }

    [RelayCommand]
    private async Task InstallHostAsync() => await RunElevatedAsync("--install", "установка").ConfigureAwait(true);

    [RelayCommand]
    private async Task RemoveHostAsync() => await RunElevatedAsync("--uninstall", "удаление").ConfigureAwait(true);

    private async Task RunElevatedAsync(string argument, string what)
    {
        if (IsHostBusy) return;

        IsHostBusy = true;
        Detail = null;

        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                Detail = "Не удалось определить путь к самому себе.";
                return;
            }

            using var process = Process.Start(new ProcessStartInfo(exe, argument)
            {
                UseShellExecute = true,
                Verb = "runas",
            });

            if (process is null)
            {
                Detail = $"Запустить {what} не удалось.";
                return;
            }

            await process.WaitForExitAsync().ConfigureAwait(true);

            if (process.ExitCode != 0)
                Detail = $"{char.ToUpperInvariant(what[0])}{what[1..]} не удалась. Подробности — в install.log.";

            // Задача поднимается не мгновенно: даём ей секунду встать и только
            // потом спрашиваем. Иначе сразу после успешной установки интерфейс
            // сказал бы «не отвечает».
            await Task.Delay(1500).ConfigureAwait(true);
            await CheckHostAsync().ConfigureAwait(true);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // Человек нажал «Нет» в запросе прав. Это не ошибка, и пугать его нечем.
            Detail = "Без прав администратора установить привилегированную часть нельзя.";
        }
        catch (Exception e)
        {
            Detail = $"{what}: {e.Message}";
        }
        finally
        {
            IsHostBusy = false;
        }
    }
}
