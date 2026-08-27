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
using Method.Core.Storage;
using Method.Core.Json;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Разбор правил маршрутизации и их место в конфигурации движка.
///
/// <para>Порядок правил здесь не косметика: движок применяет первое подошедшее,
/// поэтому проверяем не только присутствие правила, но и его позицию — правило
/// человека обязано стоять после адреса ноды и до частных адресов.</para>
/// </summary>
public class RouteRuleTests
{
    [Theory]
    [InlineData("example.com")]
    [InlineData("  Example.COM  ")]
    [InlineData("sub.example.com.")]
    public void Домен_разбирается_и_приводится_к_нижнему_регистру(string input)
    {
        var rule = RouteRule.TryParse(input, RouteAim.Direct);

        Assert.NotNull(rule);
        Assert.Equal(RouteMatch.Domain, rule.Match);
        Assert.Equal(input.Trim().Trim('.').ToLowerInvariant(), rule.Value);
    }

    [Theory]
    [InlineData("192.168.0.0/16", "192.168.0.0/16")]
    [InlineData("8.8.8.8", "8.8.8.8/32")]           // одиночному адресу дописываем маску
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("::1", "::1/128")]
    public void Адрес_разбирается_в_диапазон(string input, string expected)
    {
        var rule = RouteRule.TryParse(input, RouteAim.Tunnel);

        Assert.NotNull(rule);
        Assert.Equal(RouteMatch.Cidr, rule.Match);
        Assert.Equal(expected, rule.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("нет точки")]
    [InlineData("example .com")]
    [InlineData("http://example.com")]
    [InlineData("192.168.0.0/48")]                  // маска шире, чем бывает у IPv4
    [InlineData("999.1.1.1")]
    public void Мусор_не_становится_правилом(string input) =>
        Assert.Null(RouteRule.TryParse(input, RouteAim.Direct));

    [Fact]
    public void Правила_встают_после_адреса_ноды_и_до_частных_адресов()
    {
        var config = SingBoxConfigBuilder.Build(Profile(), new SingBoxConfigBuilder.Options
        {
            ClashSecret = "секрет",
            Rules =
            [
                RouteRule.TryParse("example.com", RouteAim.Direct)!,
                RouteRule.TryParse("10.0.0.0/8", RouteAim.Tunnel)!,
            ],
        });

        var rules = Rules(config);

        // Служебные, адрес ноды, два наших, частные адреса.
        Assert.Equal(6, rules.Length);

        Assert.Equal("direct", rules[3]["outbound"]);
        Assert.Equal(new[] { "example.com" }, rules[3]["domain_suffix"]);

        Assert.Equal("proxy", rules[4]["outbound"]);
        Assert.Equal(new[] { "10.0.0.0/8" }, rules[4]["ip_cidr"]);

        Assert.True(rules[5].ContainsKey("ip_is_private"));
    }

    [Fact]
    public void Выключенное_правило_в_конфигурацию_не_попадает()
    {
        var rule = RouteRule.TryParse("example.com", RouteAim.Direct)! with { Enabled = false };

        var config = SingBoxConfigBuilder.Build(Profile(), new SingBoxConfigBuilder.Options
        {
            ClashSecret = "секрет",
            Rules = [rule],
        });

        Assert.DoesNotContain(Rules(config), r => r.ContainsKey("domain_suffix"));
    }

    [Fact]
    public void Без_правил_конфигурация_прежняя()
    {
        var config = SingBoxConfigBuilder.Build(Profile(), new SingBoxConfigBuilder.Options { ClashSecret = "секрет" });

        Assert.Equal(4, Rules(config).Length);
    }

    /// <summary>Правила сохраняются и читаются обратно вместе с настройками.</summary>
    [Fact]
    public void Правила_переживают_запись_и_чтение()
    {
        var settings = new AppSettings
        {
            Rules = [RouteRule.TryParse("example.com", RouteAim.Tunnel)!],
        };

        var json = JsonSerializer.Serialize(settings, MethodJson.Options);
        var back = JsonSerializer.Deserialize<AppSettings>(json, MethodJson.Options);

        Assert.NotNull(back);
        Assert.Single(back.Rules);
        Assert.Equal("example.com", back.Rules[0].Value);
        Assert.Equal(RouteAim.Tunnel, back.Rules[0].Aim);
    }

    private static Dictionary<string, object?>[] Rules(Dictionary<string, object?> config)
    {
        var route = Assert.IsType<Dictionary<string, object?>>(config["route"]);
        var rules = Assert.IsType<object[]>(route["rules"]);

        return rules.Cast<Dictionary<string, object?>>().ToArray();
    }

    private static ServerProfile Profile() => new()
    {
        Name = "Узел",
        Host = "node.example.net",
        Port = 443,
        Parameters = ServerParameters.For(new Hysteria2Parameters
        {
            Password = "пароль",
            Sni = "node.example.net",
            AllowInsecure = true,
        }),
    };
}
