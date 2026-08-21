// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Method.Core.Models;
using Method.Core.Subscriptions;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Ответы подписки подставные: настоящая сеть в тестах делает их зависимыми от
/// чужого сервера и от того, оплачена ли подписка сегодня.
/// </summary>
public class SubscriptionServiceTests
{
    private const string Hysteria2Link =
        "hysteria2://shared%3As3cret@203.0.113.10:8443?sni=example.com#US%20East";

    private const string VlessLink =
        "vless://11111111-2222-3333-4444-555555555555@203.0.113.20:443" +
        "?security=reality&pbk=PUBKEY&sni=www.example.org&type=grpc&serviceName=svc#FI";

    // MARK: Форматы тела

    [Fact]
    public async Task Список_ссылок_в_base64_разбирается()
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Hysteria2Link}\n{VlessLink}"));
        var result = await Fetch(Ok(body));

        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal("US East", result.Profiles[0].Name);
        Assert.Equal(VpnProtocol.VlessReality, result.Profiles[1].Protocol);
    }

    [Fact]
    public async Task Список_ссылок_открытым_текстом_разбирается()
    {
        var result = await Fetch(Ok($"{Hysteria2Link}\n{VlessLink}"));
        Assert.Equal(2, result.Profiles.Count);
    }

    [Fact]
    public async Task Ссылки_вперемешку_с_пояснениями_находятся()
    {
        // Панели кладут ссылки и в одну строку, и вперемешку с текстом.
        var result = await Fetch(Ok($"Ваши серверы: {Hysteria2Link} и ещё {VlessLink} — приятного пользования"));
        Assert.Equal(2, result.Profiles.Count);
    }

    [Fact]
    public async Task Конфигурация_singbox_разбирается()
    {
        const string json = """
            {
              "outbounds": [
                {"type":"hysteria2","tag":"US","server":"203.0.113.10","server_port":8443,
                 "password":"shared:s3cret","tls":{"enabled":true,"server_name":"example.com","insecure":true}},
                {"type":"vless","tag":"FI","server":"203.0.113.20","server_port":443,
                 "uuid":"11111111-2222-3333-4444-555555555555",
                 "tls":{"server_name":"www.example.org","reality":{"public_key":"PUBKEY","short_id":"ab"},
                        "utls":{"fingerprint":"chrome"}},
                 "transport":{"type":"grpc","service_name":"svc"}},
                {"type":"direct","tag":"direct"}
              ]
            }
            """;

        var result = await Fetch(Ok(json));

        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal("shared:s3cret", result.Profiles[0].Parameters.Hysteria2!.Password);
        Assert.True(result.Profiles[0].Parameters.Hysteria2!.AllowInsecure);

        var vless = result.Profiles[1].Parameters.VlessReality!;
        Assert.Equal("svc", vless.GrpcServiceName);
        // Vision несовместим с gRPC — flow не должен приехать вместе с транспортом.
        Assert.Null(vless.Flow);
    }

    [Fact]
    public async Task Yaml_от_clash_разбирается()
    {
        const string yaml = """
            mixed-port: 7890
            proxies:
              - name: "FI Reality"
                type: vless
                server: 203.0.113.20
                port: 443
                uuid: 11111111-2222-3333-4444-555555555555
                servername: www.example.org
                public-key: PUBKEY
                short-id: ab
              - name: "US Hysteria"
                type: hysteria2
                server: 203.0.113.10
                port: 8443
                password: s3cret
                skip-cert-verify: true
              - name: "Чужой"
                type: vmess
                server: 203.0.113.99
                port: 443
            rules:
              - MATCH,DIRECT
            """;

        var result = await Fetch(Ok(yaml));

        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal("FI Reality", result.Profiles[0].Name);
        Assert.True(result.Profiles[1].Parameters.Hysteria2!.AllowInsecure);
        // Неподдерживаемый узел посчитан, а не выброшен молча.
        Assert.Equal(1, result.SkippedCount);
    }

    // MARK: Заголовки

    [Fact]
    public async Task Имя_и_объявление_читаются_из_заголовков()
    {
        var title = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("ByeBye RKN"));

        var result = await Fetch(Ok(Hysteria2Link, response =>
        {
            response.Headers.TryAddWithoutValidation("profile-title", title);
            response.Headers.TryAddWithoutValidation("announce", "Break the network barriers");
            response.Headers.TryAddWithoutValidation("profile-update-interval", "2");
        }));

        Assert.Equal("ByeBye RKN", result.Title);
        Assert.Equal("ByeBye RKN", result.SuggestedName);
        Assert.Equal("Break the network barriers", result.Announce);
        Assert.Equal(2, result.UpdateIntervalHours);
    }

    [Fact]
    public async Task Трафик_и_срок_читаются_из_заголовка()
    {
        var result = await Fetch(Ok(Hysteria2Link, response =>
            response.Headers.TryAddWithoutValidation(
                "Subscription-Userinfo", "upload=100; download=200; total=1000; expire=1767225600")));

        Assert.Equal(300, result.TrafficUsed);
        Assert.Equal(1000, result.TrafficTotal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767225600), result.ExpiresAt);
    }

    [Fact]
    public async Task Адрес_из_заголовка_принимается_только_по_http()
    {
        // Заголовок приходит с чужого сервера. Открыть по нему произвольную схему —
        // значит дать недоверенному вводу право запускать что-то на машине.
        var result = await Fetch(Ok(Hysteria2Link, response =>
        {
            response.Headers.TryAddWithoutValidation("profile-web-page-url", "file:///C:/Windows/System32/cmd.exe");
            response.Headers.TryAddWithoutValidation("support-url", "https://example.com/help");
        }));

        Assert.Null(result.WebPageUrl);
        Assert.Equal("https://example.com/help", result.SupportUrl);
    }

    [Fact]
    public async Task Идентификатор_машины_уходит_в_заголовках()
    {
        var handler = new StubHandler(_ => Ok(Hysteria2Link)());
        await Service(handler).FetchAsync("https://sub.example.com/list");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("TEST-HWID", request.Headers.GetValues("X-HWID").Single());
        Assert.Equal("Windows", request.Headers.GetValues("X-Device-Platform").Single());
    }

    [Fact]
    public async Task Подстановка_устройства_в_адресе_раскрывается()
    {
        var handler = new StubHandler(_ => Ok(Hysteria2Link)());
        await Service(handler).FetchAsync("https://sub.example.com/sub/{hwid}");

        Assert.Equal("https://sub.example.com/sub/TEST-HWID", handler.Requests[0].RequestUri!.ToString());
    }

    // MARK: Отказы

    [Fact]
    public async Task Сообщение_провайдера_показывается_как_есть()
    {
        // Панели зашивают сообщение в имя фиктивного узла — так его увидит любой клиент.
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "vless://00000000-0000-0000-0000-000000000000@0.0.0.0:443#%D0%9E%D0%BF%D0%BB%D0%B0%D1%82%D0%B8%D1%82%D0%B5%20%D0%BF%D0%BE%D0%B4%D0%BF%D0%B8%D1%81%D0%BA%D1%83"));

        var e = await Assert.ThrowsAsync<SubscriptionException>(() => Fetch(Ok(body)));

        Assert.Equal(SubscriptionFailure.ProviderMessage, e.Failure);
        Assert.Contains("Оплатите подписку", e.Message);
    }

    [Fact]
    public async Task Страница_вместо_конфигурации_объясняется_заголовком_страницы()
    {
        const string html = "<html><head><title>403 Forbidden</title></head><body>nope</body></html>";
        var e = await Assert.ThrowsAsync<SubscriptionException>(() => Fetch(Ok(html)));

        Assert.Equal(SubscriptionFailure.Network, e.Failure);
        Assert.Contains("403 Forbidden", e.Message);
    }

    [Fact]
    public async Task Отказ_сервера_называет_код()
    {
        var e = await Assert.ThrowsAsync<SubscriptionException>(
            () => Fetch(() => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        Assert.Equal(SubscriptionFailure.Network, e.Failure);
        Assert.Contains("403", e.Message);
    }

    [Fact]
    public async Task Формат_xray_называется_прямо()
    {
        // Разбирать его мы не умеем, и «пусто» здесь было бы враньём.
        const string json = """[{"routing":{},"outbounds":[]}]""";
        var e = await Assert.ThrowsAsync<SubscriptionException>(() => Fetch(Ok(json)));

        Assert.Equal(SubscriptionFailure.UnsupportedFormat, e.Failure);
        Assert.Contains("Xray", e.Message);
    }

    [Fact]
    public async Task Пустая_подписка_называется_пустой()
    {
        var e = await Assert.ThrowsAsync<SubscriptionException>(() => Fetch(Ok("")));
        Assert.Equal(SubscriptionFailure.Empty, e.Failure);
    }

    [Fact]
    public async Task Неразобранные_ссылки_считаются()
    {
        // Подписка привезла на узел меньше — об этом обязаны сказать числом.
        var body = $"{Hysteria2Link}\nvless://uuid@203.0.113.20:443?security=tls&sni=s#Broken";
        var result = await Fetch(Ok(body));

        Assert.Single(result.Profiles);
        Assert.Equal(1, result.SkippedCount);
    }

    // MARK: Подставные части

    private static Func<HttpResponseMessage> Ok(string body, Action<HttpResponseMessage>? headers = null) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("text/plain")),
        };
        headers?.Invoke(response);
        return response;
    };

    private static Task<SubscriptionFetchResult> Fetch(Func<HttpResponseMessage> respond) =>
        Service(new StubHandler(_ => respond())).FetchAsync("https://sub.example.com/list");

    private static SubscriptionService Service(StubHandler handler) =>
        new(new HttpClient(handler), new FakeDevice());

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeDevice : IDeviceIdentity
    {
        public string Hwid => "TEST-HWID";
        public string ShortHwid => "TEST-HWID";
        public string HashedHwid => "0000";
        public string DeviceName => "Тестовая машина";
        public string Platform => "Windows";
    }
}
