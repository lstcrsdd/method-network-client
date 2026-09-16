// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Method.Core.Core;
using Method.Core.Models;

namespace Method.Core.Services;

/// <summary>Итог проверки узла: прошёл ли через него настоящий запрос и за сколько.</summary>
public sealed record NodeProbeResult(bool Reachable, int? Milliseconds, string? Failure)
{
    public static NodeProbeResult Ok(int ms) => new(true, ms, null);

    public static NodeProbeResult Fail(string why) => new(false, null, why);
}

/// <summary>
/// Проверка узла делом: пробный движок на этом узле и запрос наружу через него.
///
/// <para><b>Почему не время рукопожатия TCP.</b> Оно врёт в обе стороны, и оба
/// раза проверено вживую. Узел, принимающий только UDP (Hysteria2), на TCP не
/// отвечает — и выглядит мёртвым при полностью рабочем сервере. А когда на машине
/// поднят любой туннель (наш или чужой), рукопожатие завершает его локальный
/// стек за ноль миллисекунд, ещё до выхода в сеть, — и живыми выглядят все
/// адреса, включая заблокированные по дороге. Три из пяти узлов, молчащих из
/// сети провайдера, показывались как «0 мс».</para>
///
/// <para><b>Почему мимо туннеля.</b> Пробный движок привязывается к физическому
/// интерфейсу. Иначе его путь к узлу завернул бы в себя поднятый туннель, и замер
/// показывал бы проходимость «VPN внутри VPN», а не то, что видит провайдер.</para>
///
/// <para>Конфигурация несёт пароль узла, поэтому лежит на диске ровно столько,
/// сколько движку нужно, чтобы её прочитать, и в каталоге пользователя.</para>
/// </summary>
public static class NodeProbe
{
    /// <summary>
    /// Куда стучимся. Страница без содержимого, отвечающая мгновенно: меряется путь,
    /// а не сайт. Та же, что панель выдаёт в подписке для проверки через прокси.
    /// </summary>
    private const string CheckUrl = "https://cp.cloudflare.com/generate_204";

    /// <summary>
    /// Запросов на узел. Первый несёт рукопожатие с самим узлом — холодное, в разы
    /// дольше рабочего, — последующие идут по уже поднятому до узла сеансу. Число
    /// берётся лучшее: его и видит человек в работе, и его же показывают другие
    /// клиенты, так что цифры можно сравнивать. Узел считается доступным, если
    /// прошёл хоть один: случайная потеря пакета не должна выглядеть блокировкой.
    /// </summary>
    private const int Attempts = 3;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(4);

    public static async Task<NodeProbeResult> CheckAsync(
        ServerProfile profile, string enginePath, string? bindInterface, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!File.Exists(enginePath)) return NodeProbeResult.Fail("движок не найден");

        var port = FreePort();
        var dir = Path.Combine(AppPaths.UserDataDir, "probe");
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, $"{Guid.NewGuid():N}.json");

        Process? engine = null;
        try
        {
            await File.WriteAllTextAsync(config, SingBoxConfigBuilder.BuildProbeJson(profile, port, bindInterface), ct)
                .ConfigureAwait(false);

            engine = Process.Start(new ProcessStartInfo(enginePath, $"run -c \"{config}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (engine is null) return NodeProbeResult.Fail("движок не запустился");

            // Вывод движка никому не нужен, но непрочитанный буфер однажды
            // заполнится, и движок встанет на записи в него.
            engine.OutputDataReceived += static (_, _) => { };
            engine.ErrorDataReceived += static (_, _) => { };
            engine.BeginOutputReadLine();
            engine.BeginErrorReadLine();

            var listening = await WaitListening(port, engine, ct).ConfigureAwait(false);
            TryDelete(config);

            if (!listening) return NodeProbeResult.Fail("движок не поднялся на узле");

            int? best = null;
            string? lastFailure = null;

            for (var i = 0; i < Attempts; i++)
            {
                var (ms, failure) = await Request(port, ct).ConfigureAwait(false);
                if (ms is { } got) best = best is { } prev ? Math.Min(prev, got) : got;
                else lastFailure = failure;

                // Первый отказ без единого успеха — узел скорее всего молчит; ждать
                // ещё два таймаута по восемь секунд незачем.
                if (best is null && i == 0 && failure == "нет ответа") break;
            }

            return best is { } found ? NodeProbeResult.Ok(found) : NodeProbeResult.Fail(lastFailure ?? "нет ответа");
        }
        finally
        {
            TryDelete(config);
            if (engine is not null)
            {
                try { engine.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                engine.Dispose();
            }
        }
    }

    private static async Task<(int? Ms, string? Failure)> Request(int port, CancellationToken ct)
    {
        // Каждый запрос — новое соединение: проба по соединению, открытому до того,
        // как узел замолчал, сказала бы «проходит» там, где уже не проходит.
        using var http = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.Zero,
        })
        {
            Timeout = RequestTimeout,
        };

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(CheckUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return ((int)watch.ElapsedMilliseconds, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "нет ответа");
        }
        catch (HttpRequestException)
        {
            return (null, "соединение оборвано");
        }
    }

    private static async Task<bool> WaitListening(int port, Process engine, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < StartTimeout)
        {
            if (engine.HasExited) return false;

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    /// Свободный порт у самой системы. Проверки идут параллельно, и выбор «первого
    /// свободного от привычного» отдавал двум пробам подряд один и тот же порт.
    /// </summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Разбор итогов по адресам. Тот же принцип, что в ядре: если на адресе не
/// проходит ни один способ, это свидетельство против адреса, а не против
/// протоколов.
/// </summary>
public static class AddressVerdict
{
    /// <summary>
    /// Адреса, на которых проверены все узлы и не прошёл ни один. Адрес, где хоть
    /// один узел не проверен, сюда не попадает: молчание неизвестного — не довод.
    /// </summary>
    public static IReadOnlySet<string> Silent(IEnumerable<(string Host, bool? Reachable)> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results
            .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.All(r => r.Reachable == false))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
