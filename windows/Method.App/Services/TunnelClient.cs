// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.IO.Pipes;
using System.Text.Json;

namespace Method.App.Services;

/// <summary>Обращение интерфейса к привилегированной части по именованному каналу.</summary>
public static class TunnelClient
{
    /// <summary>Состояние с той стороны канала.</summary>
    public enum HostStatus
    {
        /// <summary>Движок запущен.</summary>
        Up,

        /// <summary>Движок явно не запущен.</summary>
        Down,

        /// <summary>До привилегированной части не достучались.</summary>
        Unreachable,
    }

    // Сроки взяты из настоящей длительности операций на той стороне. Запуск делает
    // остановку прежнего процесса, уборку адаптера с паузами и проверку
    // конфигурации — это десятки секунд. Запас нужен, чтобы не оборвать законную
    // долгую операцию, но он обязан быть конечным.
    private const int QuickTotalMs = 4_000;
    private const int StopTotalMs = 20_000;
    private const int StartTotalMs = 60_000;

    /// <summary>Привилегированная часть отвечает?</summary>
    public static async Task<bool> IsAvailableAsync() =>
        (await SendAsync(new Ipc.Request { Cmd = "version" }, 700, QuickTotalMs).ConfigureAwait(false))?.Ok == true;

    /// <summary>
    /// Поднять туннель. Предупреждение означает, что туннель поднят, но часть
    /// защиты не применилась. Игнорировать его нельзя: интерфейс покажет защиту,
    /// которой нет.
    /// </summary>
    public static async Task<(bool Ok, string? Error, string? Warning)> StartAsync(
        string config, bool killSwitch = false, bool blockLanDns = false)
    {
        var response = await SendAsync(
            new Ipc.Request { Cmd = "start", Config = config, KillSwitch = killSwitch, BlockLanDns = blockLanDns },
            4000, StartTotalMs).ConfigureAwait(false);

        return response is null
            ? (false, "привилегированная часть недоступна — Method не установлен", null)
            : (response.Ok, response.Error, response.Warning);
    }

    /// <summary>
    /// Остановить. Предупреждение здесь — про неснятую блокировку исходящего:
    /// туннеля уже нет, а интернета может не быть, и об этом обязаны сказать вслух.
    /// </summary>
    public static async Task<(bool Ok, string? Error, string? Warning)> StopAsync()
    {
        var response = await SendAsync(new Ipc.Request { Cmd = "stop" }, 1500, StopTotalMs).ConfigureAwait(false);

        return response is null
            ? (false, "привилегированная часть недоступна", null)
            : (response.Ok, response.Error, response.Warning);
    }

    public static async Task<(bool Ok, string? Error)> ReconnectAsync()
    {
        var response = await SendAsync(new Ipc.Request { Cmd = "reconnect" }, 8000, StartTotalMs).ConfigureAwait(false);

        return response is null
            ? (false, "привилегированная часть недоступна")
            : (response.Ok, response.Error);
    }

    /// <summary>
    /// Трёхзначное состояние: отличает «туннель мёртв» от «до канала на миг не
    /// достучались». Сторож здоровья не должен переподключаться из-за второго.
    /// </summary>
    public static async Task<HostStatus> GetStatusAsync()
    {
        var response = await SendAsync(new Ipc.Request { Cmd = "status" }, 1500, QuickTotalMs).ConfigureAwait(false);
        if (response is null) return HostStatus.Unreachable;
        return response.Running ? HostStatus.Up : HostStatus.Down;
    }

    /// <summary>
    /// Отправить команду и дождаться ответа.
    ///
    /// <para>Срок общий — на подключение, запись и чтение. Ограничивать только
    /// подключение недостаточно: та сторона может отвечать долго, и если она
    /// подвиснет, вызывающий будет ждать вечно. В интерфейсе это худший исход —
    /// флаг «идёт переподключение» не снимается никогда, и разовый сбой
    /// превращается в постоянный.</para>
    /// </summary>
    private static async Task<Ipc.Response?> SendAsync(Ipc.Request request, int connectTimeoutMs, int totalTimeoutMs)
    {
        using var cts = new CancellationTokenSource(totalTimeoutMs);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", Ipc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(connectTimeoutMs, cts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cts.Token).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            return string.IsNullOrEmpty(line) ? null : JsonSerializer.Deserialize<Ipc.Response>(line);
        }
        catch
        {
            // Любой отказ — нет привилегированной части, истёк срок, ответ не
            // разобрать — для вызывающего одинаков: ответа нет. Различать их здесь
            // нечем и незачем.
            return null;
        }
    }
}
