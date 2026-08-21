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
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Модель профиля общая с macOS и Android. Здесь проверяется не «сериализуется ли
/// вообще», а совпадает ли форма записи с той, что читают другие платформы.
/// </summary>
public class ServerProfileJsonTests
{
    /// <summary>Профиль в том виде, в каком его пишет клиент macOS.</summary>
    private const string MacOsShaped = """
        {
          "id": "0D9B1B4E-6C1A-4C9E-9E39-6A2E9F0A1B2C",
          "name": "US East",
          "protocol": "hysteria2",
          "host": "203.0.113.10",
          "port": 8443,
          "parameters": {
            "kind": "hysteria2",
            "hysteria2": {
              "password": "shared:s3cret",
              "sni": "example.com",
              "allowInsecure": true,
              "alpn": ["h3"]
            }
          },
          "featuredLabel": "VIP"
        }
        """;

    [Fact]
    public void Профиль_с_macos_читается_целиком()
    {
        var profile = JsonSerializer.Deserialize<ServerProfile>(MacOsShaped, MethodJson.Options)!;

        Assert.Equal(Guid.Parse("0D9B1B4E-6C1A-4C9E-9E39-6A2E9F0A1B2C"), profile.Id);
        Assert.Equal("US East", profile.Name);
        Assert.Equal(VpnProtocol.Hysteria2, profile.Protocol);
        Assert.Equal("203.0.113.10", profile.Host);
        Assert.Equal(8443, profile.Port);
        Assert.Equal("VIP", profile.FeaturedLabel);
        Assert.True(profile.IsFeatured);

        var p = profile.Parameters.Hysteria2!;
        Assert.Equal("shared:s3cret", p.Password);
        Assert.Equal("example.com", p.Sni);
        Assert.True(p.AllowInsecure);
        Assert.Equal(new[] { "h3" }, p.Alpn);
    }

    [Fact]
    public void Протокол_пишется_строкой_а_не_числом()
    {
        // Число сломало бы чтение на macOS и, что хуже, пережило бы перестановку
        // значений в перечислении, молча поменяв смысл сохранённых файлов.
        var json = JsonSerializer.Serialize(Sample(), MethodJson.Options);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("hysteria2", doc.RootElement.GetProperty("protocol").GetString());
        Assert.Equal("hysteria2", doc.RootElement.GetProperty("parameters").GetProperty("kind").GetString());
    }

    [Fact]
    public void Пустые_поля_не_пишутся()
    {
        var json = JsonSerializer.Serialize(Sample(), MethodJson.Options);
        using var doc = JsonDocument.Parse(json);

        var hysteria2 = doc.RootElement.GetProperty("parameters").GetProperty("hysteria2");
        Assert.False(hysteria2.TryGetProperty("obfsPassword", out _));
        Assert.False(hysteria2.TryGetProperty("upMbps", out _));
        Assert.False(doc.RootElement.TryGetProperty("featuredLabel", out _));
    }

    [Theory]
    [MemberData(nameof(ВсеПротоколы))]
    public void Профиль_переживает_запись_и_чтение(ServerProfile original)
    {
        var json = JsonSerializer.Serialize(original, MethodJson.Options);
        var restored = JsonSerializer.Deserialize<ServerProfile>(json, MethodJson.Options)!;

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Параметры_не_от_того_протокола_отвергаются()
    {
        // kind обещает Trojan, а внутри Hysteria2. Такой профиль не падает при
        // подключении — он подключается не туда, поэтому чтение обязано отказать.
        const string broken = """
            {"kind":"trojan","hysteria2":{"password":"x"}}
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ServerParameters>(broken, MethodJson.Options));
    }

    [Fact]
    public void Неизвестный_протокол_отвергается()
    {
        const string broken = """
            {"kind":"vmess","vmess":{"id":"x"}}
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ServerParameters>(broken, MethodJson.Options));
    }

    [Fact]
    public void Профиль_без_обязательного_поля_отвергается()
    {
        // У Hysteria2 нет пароля. Прочитать «как-нибудь» значит собрать
        // конфигурацию, которая не аутентифицируется, и искать причину в сети.
        const string broken = """
            {"kind":"hysteria2","hysteria2":{"sni":"example.com"}}
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ServerParameters>(broken, MethodJson.Options));
    }

    [Fact]
    public void Кириллица_в_имени_остаётся_читаемой()
    {
        // Файл открывают глазами, а имена узлов почти всегда с кириллицей и «·».
        // Флаг страны при этом уезжает в пару суррогатов — так работает ослабленный
        // кодировщик, и это допустимо: следующий тест проверяет, что имя от такой
        // записи не портится.
        var profile = Sample() with { Name = "🇫🇮 Финляндия · gRPC" };
        var json = JsonSerializer.Serialize(profile, MethodJson.Options);

        Assert.Contains("Финляндия · gRPC", json);
    }

    [Fact]
    public void Флаг_страны_переживает_запись_и_чтение()
    {
        var profile = Sample() with { Name = "🇫🇮 Финляндия" };
        var json = JsonSerializer.Serialize(profile, MethodJson.Options);
        var restored = JsonSerializer.Deserialize<ServerProfile>(json, MethodJson.Options)!;

        Assert.Equal("🇫🇮 Финляндия", restored.Name);
    }

    public static TheoryData<ServerProfile> ВсеПротоколы() =>
    [
        Sample(),
        new ServerProfile
        {
            Name = "FI gRPC",
            Host = "203.0.113.20",
            Port = 443,
            Parameters = ServerParameters.For(new VlessRealityParameters
            {
                Uuid = "11111111-2222-3333-4444-555555555555",
                Sni = "www.example.org",
                PublicKey = "PUBKEY",
                ShortId = "a1b2",
                GrpcServiceName = "grpc-svc",
            }),
        },
        new ServerProfile
        {
            Name = "Trojan WS",
            Host = "trojan.example.net",
            Port = 443,
            Parameters = ServerParameters.For(new TrojanParameters
            {
                Password = "secret",
                Sni = "trojan.example.net",
                WsPath = "/ws",
                WsHost = "cdn.example.net",
            }),
        },
        new ServerProfile
        {
            Name = "SS 2022",
            Host = "203.0.113.30",
            Port = 8388,
            Parameters = ServerParameters.For(new ShadowsocksParameters
            {
                Method = "2022-blake3-aes-128-gcm",
                Password = "aB+cd/efGH12345678ijkl==",
            }),
        },
    ];

    private static ServerProfile Sample() => new()
    {
        Id = Guid.Parse("0D9B1B4E-6C1A-4C9E-9E39-6A2E9F0A1B2C"),
        Name = "US East",
        Host = "203.0.113.10",
        Port = 8443,
        Parameters = ServerParameters.For(new Hysteria2Parameters
        {
            Password = "shared:s3cret",
            Sni = "example.com",
            AllowInsecure = true,
        }),
    };
}
