// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net.NetworkInformation;
using Avalonia.Threading;
using Method.App.Services;

namespace Method.App.ViewModels;

/// <summary>
/// Сторож проходимости: идёт ли трафик через поднятый туннель.
///
/// <para>Сторож живости рядом спрашивает привилегированную часть, жив ли движок,
/// — и в том и беда. Движок бывает жив при мёртвом узле: адаптер поднят,
/// маршрут по умолчанию ведёт в туннель, весь трафик уходит внутрь и там
/// пропадает. Снаружи это «интернета нет», а клиент показывает «Подключено» и
/// молчит. Ровно это и случилось на живой машине: восемьдесят секунд ни одного
/// ответа, начиная с DNS, — и ни слова в интерфейсе.</para>
///
/// <para>Поэтому здесь проверка не состоянием, а делом: короткий запрос наружу.
/// Проходит — всё в порядке. Не проходит два раза подряд — говорим человеку.
/// Дальше, если ему разрешено чинить самому, клиент пробует переподключиться, а
/// если и это не помогло — уходит на другой узел.</para>
///
/// <para>Разделять «узел умер» и «у машины отвалился Wi-Fi» приходится отдельно:
/// изнутри туннеля они выглядят одинаково. Помогает сама система — когда линка
/// нет вовсе, чинить туннель бессмысленно, и мы просто говорим, что сети нет.</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Свой ход к внешнему миру, отдельный от того, которым спрашивается адрес.
    ///
    /// <para>Соединения не переживают запрос: <see cref="SocketsHttpHandler.PooledConnectionLifetime"/>
    /// в ноль. Иначе проба пошла бы по уже открытому соединению, поднятому до
    /// того, как узел замолчал, — и сказала бы «трафик идёт» ровно там, где его
    /// нет. На этом уже обжигались при проверке переключения полос.</para>
    /// </summary>
    private static readonly HttpClient Reach = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(7),
    };

    /// <summary>
    /// Куда стучимся. Два разных хозяина, а не один: первый может лежать сам по
    /// себе, и тогда его молчание — не приговор туннелю. Ответ засчитывается
    /// любой, вплоть до отказа в доступе: важно, что он вообще дошёл.
    /// </summary>
    private static readonly string[] Outside =
    [
        "https://www.gstatic.com/generate_204",
        "https://api.ipify.org",
    ];

    /// <summary>Как часто проверяем. Полминуты: запрос копеечный, а тишина дорогая.</summary>
    private static readonly TimeSpan ReachEvery = TimeSpan.FromSeconds(30);

    /// <summary>После скольких отказов подряд говорим человеку.</summary>
    private const int TellAfter = 2;

    /// <summary>
    /// После скольких — переподключаемся. Позже, чем говорим: пока автоматика
    /// ведёт полосу, у неё есть свой круг замеров, и она успевает перевести
    /// трафик на живой узел без разрыва туннеля.
    /// </summary>
    private const int ReconnectAfter = 4;

    /// <summary>
    /// После скольких — уходим на другой узел. Переподключение к этому времени
    /// уже было и не помогло: значит дело не в движке, а в самом узле.
    /// </summary>
    private const int SwitchAfter = 6;

    private DispatcherTimer? _reach;

    /// <summary>Сколько проверок подряд наружу не вышло.</summary>
    private int _mutedInARow;

    /// <summary>Уже сказали про тишину. Второй раз — шум.</summary>
    private bool _toldMute;

    private void StartReachWatch()
    {
        _reach = new DispatcherTimer { Interval = ReachEvery };
        _reach.Tick += async (_, _) => await CheckReachAsync().ConfigureAwait(true);
        _reach.Start();
    }

    private async Task CheckReachAsync()
    {
        // Пока туннель трогаем мы сами, проверять нечего: в эти секунды его и не
        // должно быть слышно.
        if (State != ConnectionState.Connected || _reconnecting || IsSettling) return;

        if (await IsTrafficFlowingAsync().ConfigureAwait(true))
        {
            if (_toldMute)
            {
                WorkJournal.Add("Проходимость", "трафик снова идёт", JournalOutcome.Ok);

                // Снимаем только собственную жалобу: чужую подпись затирать нельзя,
                // под ней может стоять предупреждение о защите.
                if (Detail == MuteText) Detail = null;
            }

            _mutedInARow = 0;
            _toldMute = false;
            return;
        }

        // Линка нет вовсе — туннель тут ни при чём, и чинить его бессмысленно.
        // Счётчик не растёт: вернётся сеть, вернётся и связь.
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            Detail = "Сеть недоступна — ни одного подключения у машины.";
            return;
        }

        _mutedInARow++;

        if (_mutedInARow == TellAfter)
        {
            _toldMute = true;
            Detail = MuteText;
            WorkJournal.Add("Проходимость", "трафик не идёт через туннель", JournalOutcome.Warning);
        }

        if (_mutedInARow < TellAfter || !AutoReconnect) return;

        if (_mutedInARow == ReconnectAfter)
        {
            await ReconnectIfNeededAsync("трафик не идёт").ConfigureAwait(true);
            return;
        }

        if (_mutedInARow >= SwitchAfter) await SwitchNodeAsync().ConfigureAwait(true);
    }

    /// <summary>Что видит человек, пока трафик стоит.</summary>
    private const string MuteText =
        "Туннель поднят, но трафик через него не проходит — узел не отвечает.";

    /// <summary>
    /// Уход на другой узел. Последнее средство: прежний молчит, переподключение
    /// к нему уже не помогло.
    ///
    /// <para>Выбор человека при этом меняется, и об этом говорится прямо — молча
    /// подменить узел значит соврать о том, через что идёт трафик.</para>
    /// </summary>
    private async Task SwitchNodeAsync()
    {
        var current = SelectedServer;

        // Лучший по известной задержке; неизмеренные идут последними, но идут:
        // молчащий узел хуже любого неизвестного.
        var next = Servers
            .Where(s => !ReferenceEquals(s, current))
            .OrderBy(s => s.Ping ?? int.MaxValue)
            .FirstOrDefault();

        if (next is null)
        {
            Detail = MuteText + " Других узлов нет.";
            return;
        }

        _mutedInARow = 0;

        WorkJournal.Add("Узел", $"переход на {next.Name}: прежний молчит", JournalOutcome.Warning);

        SelectedServer = next;
        Detail = $"Узел не отвечал — перешли на {next.Name}.";

        await DisconnectAsync().ConfigureAwait(true);
        await ConnectAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Проба делом. Заголовки, без тела: нам нужен сам факт ответа, а не его
    /// содержимое.
    /// </summary>
    private static async Task<bool> IsTrafficFlowingAsync()
    {
        foreach (var url in Outside)
        {
            try
            {
                using var answer = await Reach
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(true);

                // Любой ответ сервера означает, что путь до него есть. Даже отказ
                // в доступе прошёл через туннель, а значит туннель работает.
                return true;
            }
            catch (Exception)
            {
                // Этот не ответил — спрашиваем следующего.
            }
        }

        return false;
    }
}
