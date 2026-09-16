// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text;
using Method.Core.Models;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Адреса в примерах — из диапазонов, отведённых под документацию (RFC 5737,
/// RFC 3849), ключи и пароли выдуманы. Ни одной настоящей ссылки здесь нет и
/// быть не может.
/// </summary>
public class ShareLinkParserTests
{
    // MARK: Hysteria2

    [Fact]
    public void Hysteria2_разбирает_полную_ссылку()
    {
        var profile = ShareLinkParser.Parse(
            "hysteria2://shared%3As3cret@203.0.113.10:8443" +
            "?sni=example.com&obfs=salamander&obfs-password=obfs123&insecure=1&upmbps=50&downmbps=200" +
            "#US%20East");

        Assert.Equal(VpnProtocol.Hysteria2, profile.Protocol);
        Assert.Equal("203.0.113.10", profile.Host);
        Assert.Equal(8443, profile.Port);
        Assert.Equal("US East", profile.Name);

        var p = Assert.IsType<Hysteria2Parameters>(profile.Parameters.Hysteria2);
        // Пароль приходит в виде «пользователь:пароль» и передаётся целиком.
        Assert.Equal("shared:s3cret", p.Password);
        Assert.Equal("example.com", p.Sni);
        Assert.True(p.AllowInsecure);
        Assert.Equal("obfs123", p.ObfsPassword);
        Assert.Equal(50, p.UpMbps);
        Assert.Equal(200, p.DownMbps);
        Assert.Equal(new[] { "h3" }, p.Alpn);
    }

    [Fact]
    public void Hysteria2_с_закреплённым_сертификатом_разрешает_самоподписанный()
    {
        // Так панель выдаёт узлы после перехода на квоты устройств: вместо
        // insecure=1 — отпечаток сертификата. Без разрешения рукопожатие с
        // самоподписанным сертификатом падает на каждом узле.
        var profile = ShareLinkParser.Parse(
            "hysteria2://pass@203.0.113.10:443?sni=example.com&obfs=salamander&obfs-password=o" +
            "&pinSHA256=AB:CD:EF:01#Pinned");

        Assert.True(profile.Parameters.Hysteria2!.AllowInsecure);
    }

    [Fact]
    public void Hysteria2_без_флагов_проверяет_сертификат()
    {
        var profile = ShareLinkParser.Parse("hysteria2://pass@203.0.113.10:443?sni=example.com#Strict");

        Assert.False(profile.Parameters.Hysteria2!.AllowInsecure);
    }

    [Fact]
    public void Hysteria2_без_порта_идёт_на_443()
    {
        var profile = ShareLinkParser.Parse("hy2://pass@203.0.113.10#Node");
        Assert.Equal(443, profile.Port);
    }

    [Fact]
    public void Hysteria2_срезает_диапазон_прыжков_по_портам()
    {
        // host:443,36000-50000 — authority, на котором разборщик URI спотыкается.
        var profile = ShareLinkParser.Parse("hysteria2://pass@203.0.113.10:443,36000-50000/?sni=example.com#Hop");

        Assert.Equal("203.0.113.10", profile.Host);
        Assert.Equal(443, profile.Port);
        Assert.Equal("example.com", profile.Parameters.Hysteria2!.Sni);
    }

    [Fact]
    public void Hysteria2_читает_alpn_списком()
    {
        var profile = ShareLinkParser.Parse("hysteria2://pass@203.0.113.10:443?alpn=h3,h2#A");
        Assert.Equal(new[] { "h3", "h2" }, profile.Parameters.Hysteria2!.Alpn);
    }

    [Fact]
    public void Hysteria2_игнорирует_пароль_обфускации_без_salamander()
    {
        // obfs другого типа мы не поддерживаем; взять от него пароль означало бы
        // собрать конфигурацию, которая выглядит рабочей и не подключается.
        var profile = ShareLinkParser.Parse("hysteria2://pass@203.0.113.10:443?obfs=none&obfs-password=x#A");
        Assert.Null(profile.Parameters.Hysteria2!.ObfsPassword);
    }

    [Fact]
    public void Hysteria2_без_пароля_отвергается()
    {
        var e = Assert.Throws<ShareLinkException>(() => ShareLinkParser.Parse("hysteria2://203.0.113.10:443#A"));
        Assert.Equal(ShareLinkFailure.MissingField, e.Failure);
        Assert.Equal("password", e.Detail);
    }

    // MARK: VLESS + Reality

    [Fact]
    public void Vless_vision_сохраняет_flow()
    {
        var profile = ShareLinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@203.0.113.20:443" +
            "?security=reality&pbk=PUBKEY&sid=a1b2&fp=chrome&sni=www.example.org&flow=xtls-rprx-vision" +
            "#FI%20Vision");

        var p = profile.Parameters.VlessReality!;
        Assert.Equal("11111111-2222-3333-4444-555555555555", p.Uuid);
        Assert.Equal("xtls-rprx-vision", p.Flow);
        Assert.Equal("www.example.org", p.Sni);
        Assert.Equal("PUBKEY", p.PublicKey);
        Assert.Equal("a1b2", p.ShortId);
        Assert.Equal("chrome", p.Fingerprint);
        Assert.Null(p.GrpcServiceName);
        Assert.Equal("FI Vision", profile.Name);
    }

    [Fact]
    public void Vless_grpc_снимает_flow_и_берёт_имя_сервиса()
    {
        // Vision работает только поверх голого TCP; вместе с gRPC он даёт отказ
        // аутентификации на стороне сервера.
        var profile = ShareLinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@203.0.113.20:443" +
            "?security=reality&pbk=PUBKEY&sni=www.example.org&type=grpc&serviceName=grpc-svc&flow=xtls-rprx-vision#FI");

        var p = profile.Parameters.VlessReality!;
        Assert.Null(p.Flow);
        Assert.Equal("grpc-svc", p.GrpcServiceName);
    }

    [Fact]
    public void Vless_grpc_без_имени_сервиса_отвергается()
    {
        var e = Assert.Throws<ShareLinkException>(() => ShareLinkParser.Parse(
            "vless://uuid@203.0.113.20:443?security=reality&pbk=K&sni=s&type=grpc#FI"));
        Assert.Equal(ShareLinkFailure.MissingField, e.Failure);
    }

    [Fact]
    public void Vless_с_неизвестным_транспортом_отвергается()
    {
        // Честный отказ вместо записи, которая добавится и не заработает.
        var e = Assert.Throws<ShareLinkException>(() => ShareLinkParser.Parse(
            "vless://uuid@203.0.113.20:443?security=reality&pbk=K&sni=s&type=ws&path=/x#FI"));
        Assert.Equal(ShareLinkFailure.UnsupportedTransport, e.Failure);
        Assert.Equal("ws", e.Detail);
    }

    [Fact]
    public void Vless_без_reality_отвергается()
    {
        var e = Assert.Throws<ShareLinkException>(() => ShareLinkParser.Parse(
            "vless://uuid@203.0.113.20:443?security=tls&sni=s#FI"));
        Assert.Equal(ShareLinkFailure.MissingField, e.Failure);
        Assert.Equal("security=reality", e.Detail);
    }

    [Fact]
    public void Vless_без_порта_отвергается()
    {
        var e = Assert.Throws<ShareLinkException>(() => ShareLinkParser.Parse(
            "vless://uuid@203.0.113.20?security=reality&pbk=K&sni=s#FI"));
        Assert.Equal(ShareLinkFailure.MissingField, e.Failure);
        Assert.Equal("port", e.Detail);
    }

    // MARK: Trojan

    [Fact]
    public void Trojan_без_sni_берёт_имя_узла()
    {
        // Trojan держится на подлинности TLS: имя обязано совпадать с сертификатом,
        // и разумного значения кроме самого host здесь нет.
        var profile = ShareLinkParser.Parse("trojan://p%40ss@trojan.example.net:443#T");

        var p = profile.Parameters.Trojan!;
        Assert.Equal("p@ss", p.Password);
        Assert.Equal("trojan.example.net", p.Sni);
        Assert.False(p.AllowInsecure);
    }

    [Fact]
    public void Trojan_ws_читает_путь_и_заголовок()
    {
        var profile = ShareLinkParser.Parse(
            "trojan://pass@trojan.example.net:443?type=ws&path=%2Fws&host=cdn.example.net&sni=trojan.example.net#T");

        var p = profile.Parameters.Trojan!;
        Assert.Equal("/ws", p.WsPath);
        Assert.Equal("cdn.example.net", p.WsHost);
    }

    [Fact]
    public void Trojan_ws_без_пути_идёт_на_корень()
    {
        var profile = ShareLinkParser.Parse("trojan://pass@trojan.example.net:443?type=ws#T");
        Assert.Equal("/", profile.Parameters.Trojan!.WsPath);
    }

    [Fact]
    public void Trojan_grpc_без_имени_сервиса_отвергается()
    {
        var e = Assert.Throws<ShareLinkException>(() => ShareLinkParser.Parse(
            "trojan://pass@trojan.example.net:443?type=grpc#T"));
        Assert.Equal(ShareLinkFailure.MissingField, e.Failure);
    }

    [Fact]
    public void Trojan_с_неизвестным_транспортом_отвергается()
    {
        var e = Assert.Throws<ShareLinkException>(() => ShareLinkParser.Parse(
            "trojan://pass@trojan.example.net:443?type=quic#T"));
        Assert.Equal(ShareLinkFailure.UnsupportedTransport, e.Failure);
    }

    // MARK: Shadowsocks

    [Fact]
    public void Shadowsocks_разбирает_sip002()
    {
        const string method = "2022-blake3-aes-128-gcm";
        // Ключ семейства 2022 — base64 с «+», «/» и выравниванием. Он обязан
        // дойти до конфигурации байт в байт.
        const string password = "aB+cd/efGH12345678ijkl==";
        var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{password}"));

        var profile = ShareLinkParser.Parse($"ss://{userInfo}@203.0.113.30:8388#SS%20Node");

        Assert.Equal(VpnProtocol.Shadowsocks, profile.Protocol);
        Assert.Equal("203.0.113.30", profile.Host);
        Assert.Equal(8388, profile.Port);
        Assert.Equal("SS Node", profile.Name);
        Assert.Equal(method, profile.Parameters.Shadowsocks!.Method);
        Assert.Equal(password, profile.Parameters.Shadowsocks!.Password);
    }

    [Fact]
    public void Shadowsocks_разбирает_sip002_без_base64()
    {
        var profile = ShareLinkParser.Parse("ss://aes-256-gcm:secret@203.0.113.30:8388#SS");

        Assert.Equal("aes-256-gcm", profile.Parameters.Shadowsocks!.Method);
        Assert.Equal("secret", profile.Parameters.Shadowsocks!.Password);
    }

    [Fact]
    public void Shadowsocks_разбирает_старую_форму_целиком_в_base64()
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:secret@203.0.113.30:8388"));
        var profile = ShareLinkParser.Parse($"ss://{body}#Legacy");

        Assert.Equal("203.0.113.30", profile.Host);
        Assert.Equal(8388, profile.Port);
        Assert.Equal("secret", profile.Parameters.Shadowsocks!.Password);
        Assert.Equal("Legacy", profile.Name);
    }

    [Fact]
    public void Shadowsocks_принимает_base64_без_выравнивания_и_url_safe()
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes("2022-blake3-aes-128-gcm:aB+cd/efGH12345678ijkl=="));
        var urlSafe = raw.Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var profile = ShareLinkParser.Parse($"ss://{urlSafe}@203.0.113.30:8388#SS");

        Assert.Equal("aB+cd/efGH12345678ijkl==", profile.Parameters.Shadowsocks!.Password);
    }

    [Fact]
    public void Shadowsocks_понимает_адрес_ipv6()
    {
        var profile = ShareLinkParser.Parse("ss://aes-256-gcm:secret@[2001:db8::1]:8388#v6");

        // В конфигурацию ядра адрес идёт без скобок.
        Assert.Equal("2001:db8::1", profile.Host);
        Assert.Equal(8388, profile.Port);
    }

    [Fact]
    public void Shadowsocks_без_имени_называется_хостом()
    {
        var profile = ShareLinkParser.Parse("ss://aes-256-gcm:secret@203.0.113.30:8388");
        Assert.Equal("203.0.113.30", profile.Name);
    }

    [Fact]
    public void Shadowsocks_отбрасывает_параметры_плагина()
    {
        var profile = ShareLinkParser.Parse("ss://aes-256-gcm:secret@203.0.113.30:8388?plugin=obfs-local#SS");
        Assert.Equal(8388, profile.Port);
    }

    // MARK: Общее

    [Fact]
    public void Пустая_ссылка_отвергается()
    {
        Assert.Equal(ShareLinkFailure.EmptyInput,
            Assert.Throws<ShareLinkException>(() => ShareLinkParser.Parse("   ")).Failure);
    }

    [Fact]
    public void Неизвестная_схема_отвергается()
    {
        var e = Assert.Throws<ShareLinkException>(() => ShareLinkParser.Parse("vmess://whatever@203.0.113.1:443"));
        Assert.Equal(ShareLinkFailure.UnsupportedScheme, e.Failure);
        Assert.Equal("vmess", e.Detail);
    }

    [Fact]
    public void Ссылка_без_имени_называется_хостом()
    {
        var profile = ShareLinkParser.Parse("hysteria2://pass@203.0.113.10:443");
        Assert.Equal("203.0.113.10", profile.Name);
    }

    // MARK: Разбор пачкой

    [Fact]
    public void Пачка_разбирает_годное_и_называет_негодное()
    {
        var text = string.Join("\n", new[]
        {
            "# profile-title: base64:VGVzdA==",
            "",
            "hysteria2://pass@203.0.113.10:443#One",
            "   ",
            "vless://uuid@203.0.113.20:443?security=tls&sni=s#Broken",
            "ss://aes-256-gcm:secret@203.0.113.30:8388#Three",
        });

        var (profiles, errors) = ShareLinkParser.ParseMany(text);

        Assert.Equal(new[] { "One", "Three" }, profiles.Select(p => p.Name));
        var error = Assert.Single(errors);
        // Номер строки — как в поле ввода: с единицы и с учётом пустых строк.
        Assert.Equal(5, error.Line);
        Assert.Equal(ShareLinkFailure.MissingField, error.Error.Failure);
    }

    [Fact]
    public void Пачка_терпит_перевод_строки_из_windows()
    {
        var (profiles, errors) = ShareLinkParser.ParseMany(
            "hysteria2://pass@203.0.113.10:443#One\r\nss://aes-256-gcm:secret@203.0.113.30:8388#Two\r\n");

        Assert.Empty(errors);
        Assert.Equal(2, profiles.Count);
    }
}
