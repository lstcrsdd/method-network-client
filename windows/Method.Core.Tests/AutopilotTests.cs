// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json;
using Method.Core.Core;
using Method.Core.Models;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Автоматика: клиент сам выбирает узел для всего, что не описано сценариями.
///
/// <para>Главное свойство, ради которого всё и затевалось, — сценарии стоят
/// выше неё. «Этот трафик только таким узлом» обязано выполняться буквально,
/// пока остальное разбирается само; иначе включённая автоматика молча отменяла
/// бы то, что человек задал руками.</para>
/// </summary>
public class AutopilotTests
{
    private static readonly ServerProfile Main = TestProfiles.Hysteria2();

    private static ServerProfile Other(string host) => TestProfiles.Hysteria2() with
    {
        Id = Guid.NewGuid(), Name = "Узел " + host, Host = host,
    };

    private static JsonElement Build(bool autopilot, RouteRule[] rules, ServerProfile[] nodes, bool core = false) =>
        JsonDocument.Parse(SingBoxConfigBuilder.BuildJson(Main, new SingBoxConfigBuilder.Options
        {
            ClashSecret = "тест",
            Rules = rules,
            Autopilot = autopilot,
            AutoNodes = nodes,
            CoreDriven = core,
        })).RootElement;

    private static JsonElement Outbound(JsonElement root, string tag) =>
        root.GetProperty("outbounds").EnumerateArray()
            .First(o => o.TryGetProperty("tag", out var t) && t.GetString() == tag);

    [Fact]
    public void Без_автоматики_последнее_слово_за_основным_узлом()
    {
        var root = Build(false, [], [Other("198.51.100.7")]);

        Assert.Equal("proxy", root.GetProperty("route").GetProperty("final").GetString());
        Assert.DoesNotContain(
            root.GetProperty("outbounds").EnumerateArray(),
            o => o.TryGetProperty("tag", out var t) && t.GetString() == "auto");
    }

    [Fact]
    public void С_автоматикой_последнее_слово_за_группой_выбора()
    {
        var root = Build(true, [], [Other("198.51.100.7"), Other("198.51.100.8")]);

        Assert.Equal("auto", root.GetProperty("route").GetProperty("final").GetString());

        var auto = Outbound(root, "auto");

        Assert.Equal("urltest", auto.GetProperty("type").GetString());
        Assert.Contains(auto.GetProperty("outbounds").EnumerateArray(), o => o.GetString() == "proxy");
    }

    [Fact]
    public void Сценарий_перекрывает_автоматику()
    {
        // Ради этого всё и делалось: «мимо туннеля» остаётся «мимо туннеля»,
        // сколько бы клиент ни выбирал узлы для остального трафика.
        var rules = new[]
        {
            new RouteRule { Value = "example.com", Match = RouteMatch.Domain, Aim = RouteAim.Direct },
        };

        var root = Build(true, rules, [Other("198.51.100.7"), Other("198.51.100.8")]);

        var rule = root.GetProperty("route").GetProperty("rules").EnumerateArray()
            .First(r => r.TryGetProperty("domain_suffix", out var d)
                        && d.EnumerateArray().Any(v => v.GetString() == "example.com"));

        Assert.Equal("direct", rule.GetProperty("outbound").GetString());
        Assert.Equal("auto", root.GetProperty("route").GetProperty("final").GetString());
    }

    [Fact]
    public void Названный_узел_сильнее_автоматики()
    {
        // «Tor только через этот узел» — самый смысл ручного сценария: клиент не
        // должен переставить такое соединение на другой узел, что бы он там ни
        // намерил.
        var node = Other("198.51.100.9");
        var rules = new[]
        {
            new RouteRule
            {
                Value = "torproject.org", Match = RouteMatch.Domain, Aim = RouteAim.Node, Node = node.Id,
            },
        };

        var root = JsonDocument.Parse(SingBoxConfigBuilder.BuildJson(Main, new SingBoxConfigBuilder.Options
        {
            ClashSecret = "тест",
            Rules = rules,
            LaneNodes = [node],
            Autopilot = true,
            AutoNodes = [node, Other("198.51.100.10")],
        })).RootElement;

        var rule = root.GetProperty("route").GetProperty("rules").EnumerateArray()
            .First(r => r.TryGetProperty("domain_suffix", out var d)
                        && d.EnumerateArray().Any(v => v.GetString() == "torproject.org"));

        var outbound = rule.GetProperty("outbound").GetString();

        Assert.StartsWith("lane-", outbound, StringComparison.Ordinal);
        Assert.NotEqual("auto", outbound);
    }

    [Fact]
    public void Под_ядром_группа_становится_переключателем()
    {
        // Ради этого этап и делался: решение ядра должно применяться поворотом
        // ручки, а не пересборкой конфигурации. У urltest такой ручки нет —
        // движок там выбирает сам и чужого выбора не принимает, так что каждое
        // решение стоило бы переподключения.
        var root = Build(true, [], [Other("198.51.100.7"), Other("198.51.100.8")], core: true);

        var auto = Outbound(root, "auto");

        Assert.Equal("selector", auto.GetProperty("type").GetString());
        Assert.Equal("auto", root.GetProperty("route").GetProperty("final").GetString());

        // Пока ядро не набрало замеров, работает выбранный человеком узел.
        Assert.Equal("proxy", auto.GetProperty("default").GetString());
        Assert.Contains(auto.GetProperty("outbounds").EnumerateArray(), o => o.GetString() == "proxy");
    }

    [Fact]
    public void Без_ядра_остаётся_выбор_движка()
    {
        // Клиент обязан работать и без библиотеки ядра: переключатель, который
        // никто не переключает, навсегда застрял бы на основном узле.
        var root = Build(true, [], [Other("198.51.100.7"), Other("198.51.100.8")]);

        Assert.Equal("urltest", Outbound(root, "auto").GetProperty("type").GetString());
    }

    [Fact]
    public void Одному_узлу_выбирать_не_из_чего()
    {
        // Группа из одного узла — это проверки по кругу ради решения, которое
        // предрешено. Тогда автоматики просто нет.
        var root = Build(true, [], []);

        Assert.Equal("proxy", root.GetProperty("route").GetProperty("final").GetString());
    }
}
