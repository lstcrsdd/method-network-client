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
/// Полосы: правило может увести соединение не «в туннель вообще», а через
/// названный узел. Ошибка здесь не падает, а тихо ведёт трафик не туда — потому
/// и проверяется по собранной конфигурации, а не по намерению.
/// </summary>
public class LaneRoutingTests
{
    private static readonly ServerProfile Main = TestProfiles.Hysteria2();

    private static ServerProfile Other() => TestProfiles.Hysteria2() with
    {
        Id = Guid.NewGuid(),
        Name = "Второй узел",
        Host = "198.51.100.7",
    };

    private static JsonElement Build(ServerProfile main, RouteRule[] rules, ServerProfile[] lanes) =>
        JsonDocument.Parse(SingBoxConfigBuilder.BuildJson(main, new SingBoxConfigBuilder.Options
        {
            ClashSecret = "тест",
            Rules = rules,
            LaneNodes = lanes,
        })).RootElement;

    private static string[] Tags(JsonElement root) =>
        root.GetProperty("outbounds").EnumerateArray()
            .Select(o => o.TryGetProperty("tag", out var tag) ? tag.GetString() ?? "" : "")
            .ToArray();

    private static JsonElement RuleFor(JsonElement root, string value) =>
        root.GetProperty("route").GetProperty("rules").EnumerateArray()
            .First(r => r.TryGetProperty("domain_suffix", out var d)
                        && d.EnumerateArray().Any(v => v.GetString() == value));

    [Fact]
    public void Узел_полосы_становится_отдельным_исходящим()
    {
        var other = Other();
        var rule = new RouteRule
        {
            Value = "telegram.org", Match = RouteMatch.Domain, Aim = RouteAim.Node, Node = other.Id,
        };

        var tags = Tags(Build(Main, [rule], [other]));

        Assert.Contains("proxy", tags);
        Assert.Contains("direct", tags);
        Assert.Contains(tags, t => t.StartsWith("lane-", StringComparison.Ordinal));
    }

    [Fact]
    public void Правило_уводит_на_свой_узел()
    {
        var other = Other();
        var rule = new RouteRule
        {
            Value = "telegram.org", Match = RouteMatch.Domain, Aim = RouteAim.Node, Node = other.Id,
        };

        var root = Build(Main, [rule], [other]);
        var outbound = RuleFor(root, "telegram.org").GetProperty("outbound").GetString();

        Assert.StartsWith("lane-", outbound, StringComparison.Ordinal);
        Assert.NotEqual("proxy", outbound);
        Assert.NotEqual("direct", outbound);
    }

    [Fact]
    public void Адрес_узла_полосы_идёт_мимо_туннеля()
    {
        // Иначе пакеты к узлу полосы ушли бы в туннель, который они же и
        // поднимают, — та же ловушка, что и с основным узлом.
        var other = Other();
        var rule = new RouteRule
        {
            Value = "telegram.org", Match = RouteMatch.Domain, Aim = RouteAim.Node, Node = other.Id,
        };

        var direct = Build(Main, [rule], [other])
            .GetProperty("route").GetProperty("rules").EnumerateArray()
            .Where(r => r.TryGetProperty("outbound", out var o) && o.GetString() == "direct")
            .SelectMany(r => r.TryGetProperty("ip_cidr", out var cidr)
                ? cidr.EnumerateArray().Select(v => v.GetString() ?? "")
                : [])
            .ToArray();

        Assert.Contains(direct, v => v.StartsWith(other.Host, StringComparison.Ordinal));
    }

    [Fact]
    public void Ссылка_на_пропавший_узел_ведёт_в_туннель_а_не_мимо()
    {
        // Узел удалили, а правило осталось. Отправить такое соединение мимо
        // туннеля значило бы раскрыть ровно то, что человек прятал.
        var rule = new RouteRule
        {
            Value = "telegram.org", Match = RouteMatch.Domain, Aim = RouteAim.Node, Node = Guid.NewGuid(),
        };

        var root = Build(Main, [rule], []);

        Assert.Equal("proxy", RuleFor(root, "telegram.org").GetProperty("outbound").GetString());
    }

    [Fact]
    public void Без_полос_конфигурация_прежняя()
    {
        // Обычные правила не должны ничего знать о полосах: у клиента без них
        // конфигурация обязана остаться такой же, как была.
        var rules = new[]
        {
            new RouteRule { Value = "example.com", Match = RouteMatch.Domain, Aim = RouteAim.Direct },
            new RouteRule { Value = "steam.exe", Match = RouteMatch.Process, Aim = RouteAim.Tunnel },
        };

        var tags = Tags(Build(Main, rules, []));

        Assert.Equal(["proxy", "direct"], tags);
    }
}
