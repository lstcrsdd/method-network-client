// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using System.Net.Sockets;

namespace Method.Core.Services;

/// <summary>
/// Задержка до узла по времени установления соединения TCP.
///
/// <para>Не ICMP: обычный ping до многих узлов не доходит вовсе — его режут по
/// дороге, — и «нет ответа» означало бы «сервер недоступен» там, где он прекрасно
/// работает. Время рукопожатия TCP меряет то же самое и меряет честно.</para>
///
/// <para><b>Чего эта цифра не говорит.</b> Она про путь до узла, а не про качество
/// маршрута: у двух транспортов на одном и том же сервере задержка под нагрузкой
/// расходится втрое. Выбирать по ней узел — то же самое, что выбирать вслепую.
/// Здесь она нужна только чтобы человек увидел, что сервер вообще отвечает.</para>
///
/// <para>Узел, принимающий только UDP, на соединение TCP не ответит, и задержка
/// окажется неизвестной при полностью рабочем сервере. Это не ошибка замера.</para>
/// </summary>
public static class PingService
{
    /// <summary>Миллисекунды до установления соединения. Пусто — не ответил в срок.</summary>
    public static async Task<int?> MeasureAsync(
        string host,
        int port,
        int timeoutMs = 2000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535) return null;

        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        var watch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            watch.Stop();
            return (int)watch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            // Отказ в соединении, неизвестное имя, сеть недоступна — для замера всё
            // это одно и то же: цифры нет.
            return null;
        }
    }
}
