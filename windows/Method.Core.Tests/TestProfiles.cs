// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Models;

namespace Method.Core.Tests;

/// <summary>
/// Профили для проверок. Адреса — из диапазонов, отведённых под документацию
/// (RFC 5737, RFC 3849), ключи и пароли выдуманы. Настоящих узлов здесь нет.
/// </summary>
public static class TestProfiles
{
    public static ServerProfile Hysteria2() => new()
    {
        Name = "US East",
        Host = "203.0.113.10",
        Port = 8443,
        Parameters = ServerParameters.For(new Hysteria2Parameters { Password = "shared:s3cret" }),
    };

    public static ServerProfile VlessVision() => new()
    {
        Name = "FI Vision",
        Host = "203.0.113.20",
        Port = 443,
        Parameters = ServerParameters.For(new VlessRealityParameters
        {
            Uuid = "11111111-2222-3333-4444-555555555555",
            Flow = "xtls-rprx-vision",
            Sni = "www.example.org",
            PublicKey = "cJ8Yr9nQvKcJ0mF3sT1uW5xZ7aB2dE4gH6iK8lN0oP4",
            ShortId = "a1b2c3d4",
        }),
    };

    public static ServerProfile VlessGrpc() => new()
    {
        Name = "FI gRPC",
        Host = "203.0.113.20",
        Port = 443,
        Parameters = ServerParameters.For(new VlessRealityParameters
        {
            Uuid = "11111111-2222-3333-4444-555555555555",
            Sni = "www.example.org",
            PublicKey = "cJ8Yr9nQvKcJ0mF3sT1uW5xZ7aB2dE4gH6iK8lN0oP4",
            ShortId = "a1b2c3d4",
            GrpcServiceName = "grpc-svc",
        }),
    };

    public static ServerProfile TrojanWs() => new()
    {
        Name = "Trojan WS",
        Host = "trojan.example.net",
        Port = 443,
        Parameters = ServerParameters.For(new TrojanParameters
        {
            Password = "secret",
            Sni = "trojan.example.net",
            Fingerprint = "chrome",
            WsPath = "/ws",
            WsHost = "cdn.example.net",
        }),
    };

    public static ServerProfile TrojanGrpc() => new()
    {
        Name = "Trojan gRPC",
        Host = "trojan.example.net",
        Port = 443,
        Parameters = ServerParameters.For(new TrojanParameters
        {
            Password = "secret",
            Sni = "trojan.example.net",
            GrpcServiceName = "grpc-svc",
        }),
    };

    public static ServerProfile Shadowsocks() => new()
    {
        Name = "SS 2022",
        Host = "203.0.113.30",
        Port = 8388,
        Parameters = ServerParameters.For(new ShadowsocksParameters
        {
            Method = "2022-blake3-aes-128-gcm",
            Password = "aB+cd/efGH12345678ijkl==",
        }),
    };

    /// <summary>Все транспорты разом — для проверок, которые обязаны пройти по каждому.</summary>
    public static IEnumerable<ServerProfile> All()
    {
        yield return Hysteria2();
        yield return VlessVision();
        yield return VlessGrpc();
        yield return TrojanWs();
        yield return TrojanGrpc();
        yield return Shadowsocks();
    }
}
