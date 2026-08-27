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
