// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json;
using Method.Core.Json;
using Method.Core.Models;
using Method.Core.Native;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Граница общего ядра.
///
/// <para>Проверки идут против настоящей библиотеки, а не заглушки: смысл
/// перехода на общее ядро именно в том, чтобы разбор был один на все
/// платформы, и подделка здесь проверяла бы нашу же выдумку.</para>
///
/// <para>Без собранной библиотеки проверки пропускаются, а не падают: клиент
/// собирается и без ядра, и красный прогон у того, кто ещё не запускал
/// <c>core/Scripts/build-windows.ps1</c>, сообщал бы не о поломке, а об
/// отсутствии необязательного шага.</para>
/// </summary>
public class MethodCoreTests
{
    /// <summary>Ссылка того же вида, что приходят в подписках.</summary>
    private const string Link =
        "hysteria2://shared%3Asecret@node.example.net:8443?sni=node.example.net&insecure=1#Узел%20FI";

    /// <summary>Узел на другой оси обхода — для проверок, где нужен запасной путь.</summary>
    private const string RealityLink =
        "vless://11111111-2222-3333-4444-555555555555@203.0.113.20:443" +
        "?security=reality&pbk=PUBKEY&sid=a1b2&fp=chrome&sni=www.example.org&flow=xtls-rprx-vision" +
        "#LT%20Vision";

    [SkippableFact]
    public void Ядро_разбирает_ссылку_так_же_как_наш_разбор()
    {
        Skip.IfNot(CoreAvailable(), "библиотека ядра не собрана");

        var mine = ShareLinkParser.Parse(Link);
        var json = MethodCore.ParseLink(Link);
        var theirs = JsonSerializer.Deserialize<ServerProfile>(json, MethodJson.Options);

        Assert.NotNull(theirs);
        Assert.Equal(mine.Host, theirs.Host);
        Assert.Equal(mine.Port, theirs.Port);
        Assert.Equal(mine.Name, theirs.Name);
        Assert.Equal(mine.Parameters.Kind, theirs.Parameters.Kind);
    }

    [SkippableFact]
    public void Кривая_строка_не_роняет_остальные()
    {
        Skip.IfNot(CoreAvailable(), "библиотека ядра не собрана");

        var body = string.Join("\n", Link, "не ссылка вовсе", Link);
        var json = MethodCore.ParseSubscription(body);

        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(2, parsed.RootElement.GetProperty("profiles").GetArrayLength());
        Assert.Equal(1, parsed.RootElement.GetProperty("errors").GetArrayLength());
    }

    [SkippableFact]
    public void Отказ_объясняется_словами()
    {
        Skip.IfNot(CoreAvailable(), "библиотека ядра не собрана");

        var failure = Assert.Throws<MethodCoreException>(() => MethodCore.ParseLink("это не ссылка"));

        Assert.NotEqual(McStatus.Ok, failure.Status);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }

    /// <summary>
    /// Конфигурация в схеме полос. Проверяем не «не упало», а то, ради чего
    /// переходим: движок получает готовый конфиг, а управляющий слой — разбор
    /// полос и маршрутов, чтобы не разбирать конфиг обратно.
    /// </summary>
    [SkippableFact]
    public void Ядро_собирает_конфигурацию_полос()
    {
        Skip.IfNot(CoreAvailable(), "библиотека ядра не собрана");

        // Двух узлов мало не потому, что так красивее: ядро требует у полосы
        // минимум два НЕЗАВИСИМЫХ способа пройти. Второй Hysteria2 запасным не
        // считается — обе оси одинаково умирают в сети, которая режет QUIC.
        var quic = MethodCore.ParseLink(Link);
        var tls = MethodCore.ParseLink(RealityLink);

        var options = """{"clashSecret":"проверка","tunStack":"gvisor"}""";

        var plan = MethodCore.BuildLanes($"[{quic},{tls}]", string.Empty, options);

        using var parsed = JsonDocument.Parse(plan);
        var root = parsed.RootElement;

        var config = root.GetProperty("config");
        Assert.True(config.TryGetProperty("outbounds", out _));
        Assert.True(config.TryGetProperty("route", out _));
        Assert.True(config.TryGetProperty("inbounds", out _));

        // Полосы и маршруты — то, чего в нашей сборке конфигурации нет вовсе.
        Assert.True(root.GetProperty("lanes").EnumerateObject().Any());
        Assert.True(root.GetProperty("routes").EnumerateObject().Any());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("defaultLaneTag").GetString()));

        // Стек передан нами и обязан дойти без подмены: с системным стеком
        // туннель на Windows поднимается, показывает «подключено» и не
        // пропускает TCP.
        var tun = config.GetProperty("inbounds").EnumerateArray()
            .First(i => i.GetProperty("type").GetString() == "tun");

        Assert.Equal("gvisor", tun.GetProperty("stack").GetString());
    }

    /// <summary>
    /// Есть ли библиотека рядом с тестами. Пробуем самым дешёвым вызовом —
    /// версией ABI: она не может провалиться по существу, поэтому отказ здесь
    /// означает ровно отсутствие библиотеки.
    /// </summary>
    private static bool CoreAvailable()
    {
        try
        {
            MethodCore.ParseLink(Link);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (MethodCoreException)
        {
            // Библиотека есть, а ссылку она не приняла — это уже предмет
            // проверки, а не повод её пропустить.
            return true;
        }
    }
}
