// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net.NetworkInformation;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Method.App.Services;
using Microsoft.Win32;

namespace Method.App.ViewModels;

/// <summary>
/// Восстановление туннеля и внешний адрес.
///
/// <para>Туннель рвётся не только от ошибок: машина уснула, сменилась сеть, ушли
/// с Wi-Fi на провод. Движок этого не переживает — адаптер остаётся, а трафик
/// стоит. Со стороны это «подключено, но не работает», и хуже него только
/// «подключено, но не работает, и клиент об этом молчит».</para>
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(8) };

    private DispatcherTimer? _watchdog;
    private bool _reconnecting;

    /// <summary>Сколько опросов подряд состояние «лежит». Одного мало — см. проверку живости.</summary>
    private int _downInARow;

    /// <summary>
    /// Поднимать туннель заново после сна и смены сети. Включено по умолчанию:
    /// без этого клиент после закрытия крышки ноутбука выглядит рабочим и не работает.
    /// </summary>
    [ObservableProperty]
    private bool _autoReconnect = true;

    /// <summary>
    /// Запускать окно при входе в систему. Значение читается у самой Windows, а не
    /// хранится у нас: две записи одного факта разъезжаются, и тумблер начинает
    /// врать о том, что на самом деле настроено.
    /// </summary>
    [ObservableProperty]
    private bool _launchAtLogin = Autostart.IsEnabled();

    partial void OnLaunchAtLoginChanged(bool value)
    {
        var problem = Autostart.SetEnabled(value);
        if (problem is null) return;

        // Не вышло — возвращаем тумблер к правде и говорим почему. Оставить его
        // включённым значило бы пообещать автозапуск, которого нет.
        Detail = $"Автозапуск не настроен: {problem}";
        Dispatcher.UIThread.Post(() => LaunchAtLogin = Autostart.IsEnabled());
    }

    private void StartWatchdog()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        // Опрос состояния — на случай, когда движок умер тихо и ни одно событие
        // системы об этом не сказало.
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _watchdog.Tick += async (_, _) => await CheckTunnelAliveAsync().ConfigureAwait(true);
        _watchdog.Start();
    }

    /// <summary>События системы приходят с чужого потока — состояние трогаем только со своего.</summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;

        // После пробуждения сеть поднимается не сразу: реконнект в ту же секунду
        // пришёлся бы на мёртвый интерфейс и просто израсходовал попытку.
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);

            if (IsSettling || State != ConnectionState.Connected) return;

            // Движок мог пережить сон живым, но с мёртвым туннелем — снаружи это
            // «подключено, а ничего не грузится». Поэтому не переподключаемся
            // вслепую, а проверяем делом: спрашиваем внешний адрес через туннель.
            // Ответил — всё в порядке, трогать нечего.
            if (await IsTunnelUsableAsync().ConfigureAwait(true)) return;

            // Движка нет — это забота надзора, он уже поднимает.
            if (await TunnelClient.GetStatusAsync().ConfigureAwait(true) == TunnelClient.HostStatus.Down) return;

            await ReconnectIfNeededAsync("после сна").ConfigureAwait(true);
        });
    }

    /// <summary>
    /// Смена сетевого окружения.
    ///
    /// <para><b>Осторожно с этим событием.</b> Поднятие нашего же адаптера туннеля —
    /// это тоже смена окружения. Переподключаться на него значит пересоздать адаптер,
    /// получить событие снова и уйти в бесконечный круг. Ровно это и случилось на
    /// живой машине: шесть переподключений за сорок секунд, движок с новым номером
    /// каждые семь секунд, и никакая проверка на нём была невозможна.</para>
    ///
    /// <para>Поэтому здесь две задвижки. Первая — окно после нашего собственного
    /// действия: события в это время порождены нами. Вторая — спрашиваем состояние
    /// и трогаем туннель, только если он действительно лежит. Переподключать живой
    /// туннель — чистый вред: связь рвётся на ровном месте.</para>
    /// </summary>
    private void OnNetworkChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);

            if (IsSettling || State != ConnectionState.Connected) return;

            // Туннель отсюда не трогаем вовсе. Упавший движок поднимает надзор
            // внутри привилегированной части, и он начинает через секунду; лезть
            // туда же значит гасить только что запущенный процесс и запускать свой.
            // На живой машине это дало два запуска подряд, а до того — бесконечный
            // круг: поднятие нашего же адаптера само по себе есть смена окружения.
            //
            // За сменой интерфейса движок следит сам и переезжает за ней. Нам
            // остаётся обновить адрес, который мог поменяться.
            await RefreshExternalIpAsync().ConfigureAwait(true);

            // А вот накопленные задержки после смены канала недействительны:
            // мерили один путь, поедем другим. Разность между ними — не джиттер,
            // и оставить её значит выбирать по числам, которых больше нет.
            NoteNetworkChanged();
        });

    /// <summary>
    /// Окно после нашего собственного действия с туннелем. Всё, что прилетает в
    /// это время, — эхо от нас самих.
    /// </summary>
    private bool IsSettling => DateTimeOffset.Now - _lastTunnelAction < TimeSpan.FromSeconds(20);

    private DateTimeOffset _lastTunnelAction = DateTimeOffset.MinValue;

    /// <summary>Отметить, что туннель трогали мы.</summary>
    internal void MarkTunnelAction() => _lastTunnelAction = DateTimeOffset.Now;

    /// <summary>
    /// Проверка живости. Состояние с той стороны трёхзначное, и это важно:
    /// «до привилегированной части не достучались» — не то же самое, что «туннель
    /// мёртв». Переподключаться из-за мгновенной недоступности канала при живом
    /// туннеле значило бы рвать связь на ровном месте.
    /// </summary>
    private async Task CheckTunnelAliveAsync()
    {
        if (State != ConnectionState.Connected || _reconnecting) return;

        var status = await TunnelClient.GetStatusAsync().ConfigureAwait(true);
        if (status != TunnelClient.HostStatus.Down)
        {
            _downInARow = 0;
            return;
        }

        // Упавший движок поднимает надзор внутри привилегированной части, и он
        // начинает через секунду. Лезть туда же с переподключением значит гасить
        // только что запущенный процесс и запускать свой — на живой машине это
        // дало два запуска подряд на ровном месте. Ждём, пока «лежит» продержится
        // два опроса подряд: к этому времени надзор уже показал, что не справился.
        if (++_downInARow < 2) return;

        if (!AutoReconnect)
        {
            // Молчать нельзя: человек видит «подключено», а туннеля нет.
            State = ConnectionState.Failed;
            Detail = "Туннель упал. Автопереподключение выключено — нажмите «Подключить».";
            return;
        }

        await ReconnectIfNeededAsync("движок остановился").ConfigureAwait(true);
    }

    private async Task ReconnectIfNeededAsync(string reason)
    {
        if (!AutoReconnect || _reconnecting) return;
        if (State is not (ConnectionState.Connected or ConnectionState.Failed)) return;

        _reconnecting = true;
        MarkTunnelAction();
        Detail = $"Переподключение {reason}…";

        try
        {
            var (ok, error) = await TunnelClient.ReconnectAsync().ConfigureAwait(true);

            if (ok)
            {
                State = ConnectionState.Connected;
                Detail = null;
                await RefreshExternalIpAsync().ConfigureAwait(true);
            }
            else
            {
                State = ConnectionState.Failed;
                Detail = error ?? "переподключиться не удалось";
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    /// <summary>
    /// Проверка делом: доходит ли трафик наружу через туннель. Состояние движка
    /// этого не говорит — процесс бывает жив при мёртвом туннеле.
    /// </summary>
    private static async Task<bool> IsTunnelUsableAsync()
    {
        try
        {
            var ip = (await Probe.GetStringAsync("https://api.ipify.org").ConfigureAwait(true)).Trim();
            return ip.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Подхватить туннель, поднятый до нас.
    ///
    /// <para>Привилегированная часть живёт отдельно от окна и переживает его
    /// закрытие — это задумано. Но окно, открытое поверх работающего туннеля,
    /// показывало «не подключено» и не давало ни отключить, ни подключиться:
    /// отключать ему было нечего, а подключение начиналось поверх живого. На
    /// живой машине это загнало человека в угол, из которого нет выхода через
    /// интерфейс.</para>
    /// </summary>
    private async Task AdoptRunningTunnelAsync()
    {
        if (await TunnelClient.GetStatusAsync().ConfigureAwait(true) != TunnelClient.HostStatus.Up) return;

        State = ConnectionState.Connected;
        _connectedAt = DateTimeOffset.Now;
        MarkTunnelAction();
        UpdateElapsed();
        _sessionTimer.Start();

        // Время сессии считается с этой минуты, а не с настоящего начала: когда
        // туннель подняли, мы не знаем — окна тогда не было. Показать неверное
        // число хуже, чем показать честное «с момента открытия окна».

        // Считать трафик нечем: секрет знает только тот, кто туннель поднимал.
        // По той же причине молчит и автоматика: без секрета к управлению
        // движком ни померить маршрут, ни переключить полосу нельзя.
        MarkTrafficUnknown();
        await StopAutopilotAsync().ConfigureAwait(true);

        Detail = "Туннель уже был поднят — клиент его подхватил.";

        await RefreshExternalIpAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Внешний адрес — единственное прямое доказательство, что трафик пошёл через
    /// туннель. Запрос идёт уже внутри него.
    /// </summary>
    private async Task RefreshExternalIpAsync()
    {
        ExternalIp = "…";

        try
        {
            var ip = (await Probe.GetStringAsync("https://api.ipify.org").ConfigureAwait(true)).Trim();
            ExternalIp = string.IsNullOrWhiteSpace(ip) ? "—" : ip;
        }
        catch (Exception)
        {
            // Не узнали — так и говорим. Показать прежний адрес было бы враньём:
            // он мог остаться от предыдущего подключения.
            ExternalIp = "неизвестен";
        }
    }
}
