// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Method.Core.Services;

namespace Method.App.ViewModels;

/// <summary>
/// Скорость и объём за сессию.
///
/// <para>Движок отдаёт накопленные суммы, а не скорость. Считаем сами разницей
/// между снимками и делим на фактическое время между ними, а не на период
/// таймера: под нагрузкой таймер плывёт, и деление на номинальную секунду
/// давало бы завышенные цифры ровно тогда, когда на них смотрят.</para>
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly HttpClient TrafficHttp = new() { Timeout = TimeSpan.FromSeconds(4) };

    private ClashApiClient? _clash;
    private DispatcherTimer? _trafficTimer;
    private TrafficTotals? _previousTotals;
    private DateTimeOffset _previousAt;
    private long _sessionStartDown;
    private long _sessionStartUp;

    /// <summary>Сколько замеров держит график. Шестьдесят — это минута истории.</summary>
    private const int HistoryLength = 60;

    private readonly List<double> _downHistory = [];
    private readonly List<double> _upHistory = [];

    [ObservableProperty]
    private string _downSpeed = "0 Б/с";

    [ObservableProperty]
    private string _upSpeed = "0 Б/с";

    [ObservableProperty]
    private string _sessionVolume = "0 Б";

    [ObservableProperty]
    private IReadOnlyList<double> _downHistoryView = [];

    [ObservableProperty]
    private IReadOnlyList<double> _upHistoryView = [];

    /// <summary>Начать считать трафик. Секрет свой на каждое подключение.</summary>
    private void StartTraffic(string secret)
    {
        _clash = new ClashApiClient(TrafficHttp, "127.0.0.1:9090", secret);
        _previousTotals = null;
        _sessionStartDown = 0;
        _sessionStartUp = 0;

        FillFlat();

        _trafficTimer ??= CreateTrafficTimer();
        _trafficTimer.Start();
    }

    private DispatcherTimer CreateTrafficTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => await PollTrafficAsync().ConfigureAwait(true);
        return timer;
    }

    /// <summary>
    /// Туннель опущен: трафика нет, и нули здесь — правда. Карточку не прячем и
    /// график не стираем: пропадая, они перекраивали страницу в момент
    /// подключения, и композиция прыгала на глазах.
    /// </summary>
    private void StopTraffic()
    {
        _trafficTimer?.Stop();
        _clash = null;
        _previousTotals = null;

        DownSpeed = "0 Б/с";
        UpSpeed = "0 Б/с";
        SessionVolume = "0 Б";

        FillFlat();
    }

    /// <summary>
    /// Туннель поднят, но считать нечем: его подняли до нас, и секрета к его
    /// статистике у нас нет. Здесь именно прочерк, а не ноль — ноль означал бы
    /// «трафика нет», тогда как трафик идёт, просто мимо наших глаз. График по
    /// той же причине пуст: плоская линия по нулю — такое же враньё.
    /// </summary>
    private void MarkTrafficUnknown()
    {
        _trafficTimer?.Stop();
        _clash = null;
        _previousTotals = null;

        DownSpeed = "—";
        UpSpeed = "—";
        SessionVolume = "—";

        _downHistory.Clear();
        _upHistory.Clear();
        PublishHistory();
    }

    /// <summary>
    /// Ровная линия по нулю во всю ширину. Без неё график начинает с двух точек,
    /// растянутых на всю карточку, и первую минуту после подключения шкала
    /// сжимается на глазах — данные вползают справа, а не перерисовываются целиком.
    /// </summary>
    private void FillFlat()
    {
        _downHistory.Clear();
        _upHistory.Clear();

        for (var i = 0; i < HistoryLength; i++)
        {
            _downHistory.Add(0);
            _upHistory.Add(0);
        }

        PublishHistory();
    }

    private async Task PollTrafficAsync()
    {
        if (_clash is null) return;

        var totals = await _clash.ReadTotalsAsync().ConfigureAwait(true);
        if (totals is null) return;

        var now = DateTimeOffset.Now;

        if (_previousTotals is null)
        {
            // Первый снимок задаёт точку отсчёта сессии: движок считает с
            // собственного запуска, а человеку интересно с этого подключения.
            _sessionStartDown = totals.Down;
            _sessionStartUp = totals.Up;
            _previousTotals = totals;
            _previousAt = now;
            return;
        }

        var seconds = (now - _previousAt).TotalSeconds;
        if (seconds <= 0.01) return;

        // Движок перезапустился — суммы поехали вниз. Считать разницу от большего
        // значения значит получить отрицательную скорость; начинаем отсчёт заново.
        if (totals.Down < _previousTotals.Down || totals.Up < _previousTotals.Up)
        {
            _sessionStartDown = totals.Down;
            _sessionStartUp = totals.Up;
            _previousTotals = totals;
            _previousAt = now;
            return;
        }

        var down = (totals.Down - _previousTotals.Down) / seconds;
        var up = (totals.Up - _previousTotals.Up) / seconds;

        _previousTotals = totals;
        _previousAt = now;

        DownSpeed = FormatSpeed(down);
        UpSpeed = FormatSpeed(up);
        SessionVolume = FormatSize(totals.Down - _sessionStartDown + (totals.Up - _sessionStartUp));

        Push(_downHistory, down);
        Push(_upHistory, up);
        PublishHistory();
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength) history.RemoveAt(0);
    }

    /// <summary>
    /// График получает новый список, а не тот же самый: изменение внутри списка
    /// перерисовку не вызывает — ему нужно увидеть другое значение свойства.
    /// </summary>
    private void PublishHistory()
    {
        DownHistoryView = _downHistory.ToArray();
        UpHistoryView = _upHistory.ToArray();
    }

    private static string FormatSpeed(double bytesPerSecond) => FormatSize(bytesPerSecond) + "/с";

    private static string FormatSize(double bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var unit = 0;

        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        // До десяти — с десятой долей: разница между 1,2 и 1,9 мегабайта заметна,
        // а между 120 и 121 — нет.
        var text = unit == 0 || bytes >= 10
            ? bytes.ToString("F0", CultureInfo.CurrentCulture)
            : bytes.ToString("F1", CultureInfo.CurrentCulture);

        return $"{text} {units[unit]}";
    }
}
