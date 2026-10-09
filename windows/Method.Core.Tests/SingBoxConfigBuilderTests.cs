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

public class SingBoxConfigBuilderTests
{
    private static readonly SingBoxConfigBuilder.Options Options = new() { ClashSecret = "тест" };

    // MARK: TUN и маршруты

    [Fact]
    public void Журнал_движка_не_пишет_каждое_соединение()
    {
        // На уровне «info» движок пишет каждое соединение, ответ DNS и путь к
        // программе, которая соединение открыла, — то есть хронику того, чем
        // человек пользуется. Для клиента, который ставят ровно затем, чтобы
        // этого не было видно, такой журнал на диске — своя же дыра.
        var log = Root(TestProfiles.Hysteria2()).GetProperty("log");

        Assert.Equal("warn", log.GetProperty("level").GetString());
    }

    [Fact]
    public void Tun_собирается_по_виндовым_правилам()
    {
        var tun = Root(TestProfiles.Hysteria2()).GetProperty("inbounds")[0];

        Assert.Equal("tun", tun.GetProperty("type").GetString());
        Assert.Equal("Method", tun.GetProperty("interface_name").GetString());
        Assert.Equal("198.18.2.1/30", tun.GetProperty("address")[0].GetString());
        Assert.Equal(1380, tun.GetProperty("mtu").GetInt32());
        Assert.True(tun.GetProperty("auto_route").GetBoolean());

        // Два отличия от macOS, оба выстраданы на живых машинах.
        Assert.Equal("gvisor", tun.GetProperty("stack").GetString());
        Assert.False(tun.GetProperty("strict_route").GetBoolean());
    }

    [Fact]
    public void У_tun_нет_адреса_ipv6()
    {
        // С ним движок падал на старте и оставлял полусозданный адаптер Wintun,
        // об который подвисала следующая попытка подключения.
        var addresses = Root(TestProfiles.Hysteria2()).GetProperty("inbounds")[0].GetProperty("address");

        Assert.Equal(1, addresses.GetArrayLength());
        Assert.DoesNotContain(":", addresses[0].GetString());
    }

    [Fact]
    public void Неизвестный_стек_отвергается_при_сборке()
    {
        // Опечатка вроде «System» с большой буквы иначе прошла бы насквозь и
        // уронила движок сырым «unknown stack» уже в рантайме. Проверено на
        // 1.13.13: sing-box check такую конфигурацию принимает (код 0), то есть
        // эта проверка — единственное, что стоит между опечаткой и живой машиной.
        var options = Options with { TunStack = "System" };

        Assert.Throws<ArgumentException>(() => SingBoxConfigBuilder.Build(TestProfiles.Hysteria2(), options));
    }

    [Theory]
    [InlineData("203.0.113.10", "ip_cidr", "203.0.113.10/32")]
    [InlineData("2001:db8::1", "ip_cidr", "2001:db8::1/128")]
    [InlineData("node.example.net", "domain", "node.example.net")]
    public void Сама_нода_идёт_мимо_туннеля(string host, string field, string expected)
    {
        // Без этого правила пакеты к ноде уходили бы в туннель, который они же и поднимают.
        var rules = Root(TestProfiles.Hysteria2() with { Host = host }).GetProperty("route").GetProperty("rules");
        var rule = rules[2];

        Assert.Equal(expected, rule.GetProperty(field)[0].GetString());
        Assert.Equal("direct", rule.GetProperty("outbound").GetString());
    }

    [Fact]
    public void Анализ_трафика_и_перехват_dns_идут_действиями_маршрута()
    {
        // В схеме 1.13 это действия маршрута, а не поля inbound.
        var rules = Root(TestProfiles.Hysteria2()).GetProperty("route").GetProperty("rules");

        Assert.Equal("sniff", rules[0].GetProperty("action").GetString());
        Assert.Equal("dns", rules[1].GetProperty("protocol").GetString());
        Assert.Equal("hijack-dns", rules[1].GetProperty("action").GetString());
    }

    [Fact]
    public void Резолвер_по_умолчанию_объявлен()
    {
        // route.default_domain_resolver обязателен с миграции 1.12; без него
        // sing-box check отказывается принимать конфигурацию.
        var route = Root(TestProfiles.Hysteria2()).GetProperty("route");

        Assert.Equal("remote", route.GetProperty("default_domain_resolver").GetProperty("server").GetString());
        Assert.Equal("proxy", route.GetProperty("final").GetString());
    }

    [Fact]
    public void Мимо_туннеля_ходит_только_резолвер_адреса_ноды()
    {
        var config = Root(TestProfiles.Hysteria2());
        var servers = config.GetProperty("dns").GetProperty("servers");

        // Внутри туннеля.
        Assert.Equal("proxy", servers[0].GetProperty("detour").GetString());
        // Снаружи — и на него ссылается только proxy-outbound.
        Assert.False(servers[1].TryGetProperty("detour", out _));
        Assert.Equal("bootstrap", servers[1].GetProperty("tag").GetString());
        Assert.Equal("remote", config.GetProperty("dns").GetProperty("final").GetString());

        var proxy = config.GetProperty("outbounds")[0];
        Assert.Equal("bootstrap", proxy.GetProperty("domain_resolver").GetProperty("server").GetString());
    }

    [Fact]
    public void DNS_через_gRPC_идёт_по_TCP_через_остальные_по_UDP()
    {
        // UDP через gRPC не проходит («read/write on closed pipe»), и DNS туннеля
        // тогда молчит целиком — Windows уходит к резолверу провайдера мимо туннеля.
        string Transport(ServerProfile p) =>
            Root(p).GetProperty("dns").GetProperty("servers")[0].GetProperty("type").GetString()!;

        Assert.Equal("tcp", Transport(TestProfiles.VlessGrpc()));
        Assert.Equal("tcp", Transport(TestProfiles.TrojanGrpc()));
        Assert.Equal("udp", Transport(TestProfiles.VlessVision()));
        Assert.Equal("udp", Transport(TestProfiles.Hysteria2()));
        Assert.Equal("udp", Transport(TestProfiles.TrojanWs()));
        Assert.Equal("udp", Transport(TestProfiles.Shadowsocks()));
    }

    // MARK: Outbound по протоколам

    [Fact]
    public void Hysteria2_переносит_пароль_обфускацию_и_лимиты()
    {
        var profile = TestProfiles.Hysteria2() with
        {
            Parameters = ServerParameters.For(new Hysteria2Parameters
            {
                Password = "shared:s3cret",
                Sni = "example.com",
                AllowInsecure = true,
                ObfsPassword = "obfs123",
                UpMbps = 50,
                DownMbps = 200,
            }),
        };

        var outbound = Root(profile).GetProperty("outbounds")[0];

        Assert.Equal("hysteria2", outbound.GetProperty("type").GetString());
        Assert.Equal("shared:s3cret", outbound.GetProperty("password").GetString());
        Assert.Equal("example.com", outbound.GetProperty("tls").GetProperty("server_name").GetString());
        Assert.True(outbound.GetProperty("tls").GetProperty("insecure").GetBoolean());
        Assert.Equal("salamander", outbound.GetProperty("obfs").GetProperty("type").GetString());
        Assert.Equal(50, outbound.GetProperty("up_mbps").GetInt32());
        Assert.Equal(200, outbound.GetProperty("down_mbps").GetInt32());
    }

    [Fact]
    public void Hysteria2_без_sni_маскируется_под_собственный_адрес()
    {
        var outbound = Root(TestProfiles.Hysteria2()).GetProperty("outbounds")[0];
        Assert.Equal("203.0.113.10", outbound.GetProperty("tls").GetProperty("server_name").GetString());
    }

    [Fact]
    public void Hysteria2_не_включает_прыжки_по_портам()
    {
        // Замер: с прыжками проходило 0–2 запроса из 4, на фиксированном порту — 4 из 4.
        var outbound = Root(TestProfiles.Hysteria2() with
        {
            Parameters = ServerParameters.For(new Hysteria2Parameters
            {
                Password = "p",
                ServerPorts = "36000:50000",
                HopInterval = "30s",
            }),
        }).GetProperty("outbounds")[0];

        Assert.False(outbound.TryGetProperty("server_ports", out _));
        Assert.False(outbound.TryGetProperty("hop_interval", out _));
    }

    [Fact]
    public void Vless_vision_ставит_flow_и_не_ставит_транспорт()
    {
        var outbound = Root(TestProfiles.VlessVision()).GetProperty("outbounds")[0];

        Assert.Equal("xtls-rprx-vision", outbound.GetProperty("flow").GetString());
        Assert.False(outbound.TryGetProperty("transport", out _));
        Assert.True(outbound.GetProperty("tls").GetProperty("reality").GetProperty("enabled").GetBoolean());
        Assert.Equal("chrome", outbound.GetProperty("tls").GetProperty("utls").GetProperty("fingerprint").GetString());
    }

    [Fact]
    public void Vless_grpc_ставит_транспорт_и_не_ставит_flow()
    {
        // Vision работает только поверх голого TCP; вместе с gRPC соединение
        // принимается, но данные не идут.
        var outbound = Root(TestProfiles.VlessGrpc()).GetProperty("outbounds")[0];

        Assert.False(outbound.TryGetProperty("flow", out _));
        Assert.Equal("grpc", outbound.GetProperty("transport").GetProperty("type").GetString());
        Assert.Equal("grpc-svc", outbound.GetProperty("transport").GetProperty("service_name").GetString());
    }

    [Fact]
    public void Trojan_ws_переносит_путь_и_заголовок()
    {
        var outbound = Root(TestProfiles.TrojanWs()).GetProperty("outbounds")[0];
        var transport = outbound.GetProperty("transport");

        Assert.Equal("ws", transport.GetProperty("type").GetString());
        Assert.Equal("/ws", transport.GetProperty("path").GetString());
        Assert.Equal("cdn.example.net", transport.GetProperty("headers").GetProperty("Host").GetString());
    }

    [Fact]
    public void Shadowsocks_передаёт_шифр_и_ключ_как_есть()
    {
        // У семейства 2022 пароль — base64-ключ строго определённой длины.
        var outbound = Root(TestProfiles.Shadowsocks()).GetProperty("outbounds")[0];

        Assert.Equal("2022-blake3-aes-128-gcm", outbound.GetProperty("method").GetString());
        Assert.Equal("aB+cd/efGH12345678ijkl==", outbound.GetProperty("password").GetString());
    }

    // MARK: Прочее

    [Fact]
    public void Секрет_clash_api_попадает_в_конфигурацию()
    {
        var clash = Root(TestProfiles.Hysteria2()).GetProperty("experimental").GetProperty("clash_api");

        Assert.Equal("127.0.0.1:9090", clash.GetProperty("external_controller").GetString());
        Assert.Equal("тест", clash.GetProperty("secret").GetString());
    }

    [Fact]
    public void Пробная_конфигурация_не_трогает_сеть_машины()
    {
        // Ни адаптера, ни маршрутов: сервер проверяется до того, как что-то менялось.
        using var doc = JsonDocument.Parse(SingBoxConfigBuilder.BuildProbeJson(TestProfiles.Hysteria2(), 18080));
        var root = doc.RootElement;

        Assert.Equal("mixed", root.GetProperty("inbounds")[0].GetProperty("type").GetString());
        Assert.Equal(18080, root.GetProperty("inbounds")[0].GetProperty("listen_port").GetInt32());
        Assert.Equal("127.0.0.1", root.GetProperty("inbounds")[0].GetProperty("listen").GetString());
        Assert.False(root.TryGetProperty("dns", out _));
        Assert.Equal("proxy", root.GetProperty("route").GetProperty("final").GetString());
    }

    [Fact]
    public void Путь_websocket_не_превращается_в_escape_последовательности()
    {
        // Конфигурацию читают глазами, когда что-то не встало.
        Assert.Contains("\"path\": \"/ws\"", SingBoxConfigBuilder.BuildJson(TestProfiles.TrojanWs(), Options));
    }

    private static JsonElement Root(ServerProfile profile)
    {
        var json = SingBoxConfigBuilder.BuildJson(profile, Options);
        // Документ живёт до конца теста: JsonElement на освобождённый документ
        // читать нельзя, поэтому разбираем в отдельный, никем не владеемый узел.
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
