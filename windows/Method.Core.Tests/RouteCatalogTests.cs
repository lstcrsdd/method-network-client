// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Core;
using Method.Core.Native;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Перевод узла в маршрут ядра.
///
/// <para>Дороже всего здесь ось обхода. Ошибка в ней не падает и не видна: ядро
/// просто считает два маршрута независимыми путями, тогда как их режет одна и
/// та же помеха, — и обещанный запасной путь существует только на бумаге.</para>
/// </summary>
public class RouteCatalogTests
{
    [Fact]
    public void Hysteria2_идёт_осью_QUIC()
    {
        Assert.Equal(CoreAxis.QuicUdp, RouteCatalog.Axis(TestProfiles.Hysteria2()));
    }

    [Fact]
    public void Reality_делится_надвое_по_транспорту()
    {
        // Vision идёт голым TCP, gRPC — потоком HTTP/2. Наблюдатель видит разное,
        // и запасными друг другу они быть могут; свести их в одну ось значило бы
        // лишить полосу второго пути.
        Assert.Equal(CoreAxis.FakeTlsTcp, RouteCatalog.Axis(TestProfiles.VlessVision()));
        Assert.Equal(CoreAxis.FakeTlsH2, RouteCatalog.Axis(TestProfiles.VlessGrpc()));

        Assert.Equal("vless-vision", RouteCatalog.Transport(TestProfiles.VlessVision()));
        Assert.Equal("vless-grpc", RouteCatalog.Transport(TestProfiles.VlessGrpc()));
    }

    [Fact]
    public void Trojan_во_всех_видах_остаётся_настоящим_TLS()
    {
        // По WebSocket или по gRPC — снаружи это подлинный TLS с настоящим
        // сертификатом. Ось у них одна, как бы ни различался транспорт.
        Assert.Equal(CoreAxis.RealTls, RouteCatalog.Axis(TestProfiles.TrojanWs()));
        Assert.Equal(CoreAxis.RealTls, RouteCatalog.Axis(TestProfiles.TrojanGrpc()));

        Assert.Equal("trojan-ws", RouteCatalog.Transport(TestProfiles.TrojanWs()));
        Assert.Equal("trojan-grpc", RouteCatalog.Transport(TestProfiles.TrojanGrpc()));
    }

    [Fact]
    public void Shadowsocks_идёт_голым_потоком()
    {
        Assert.Equal(CoreAxis.RawStream, RouteCatalog.Axis(TestProfiles.Shadowsocks()));
    }

    [Fact]
    public void Опознаватель_маршрута_не_привязан_к_имени()
    {
        // Имя приходит из подписки и меняется вместе с ней; ядро же хранит по
        // этому ключу накопленную историю замеров. Смена имени не должна
        // обнулять то, что о маршруте уже известно.
        var profile = TestProfiles.Hysteria2();
        var renamed = profile with { Name = "🇫🇮 Совсем другое имя" };

        Assert.Equal(RouteCatalog.Describe(profile).Id, RouteCatalog.Describe(renamed).Id);
    }

    [Fact]
    public void Один_сервер_под_двумя_протоколами_даёт_один_узел()
    {
        // Ядру важно знать, что два маршрута ведут в одну точку отказа: сервер
        // упал — упали оба, и держать второй как запасной бессмысленно. При этом
        // маршруты остаются разными: у них разная задержка и разная судьба под
        // помехой.
        var vision = TestProfiles.VlessVision();
        var grpc = TestProfiles.VlessGrpc();

        Assert.Equal(RouteCatalog.Describe(vision).Node, RouteCatalog.Describe(grpc).Node);
        Assert.NotEqual(RouteCatalog.Describe(vision).Id, RouteCatalog.Describe(grpc).Id);
    }

    [Fact]
    public void Страна_берётся_из_имени()
    {
        var profile = TestProfiles.Hysteria2() with { Name = "🇫🇮 Хельсинки" };

        Assert.Equal("FI", RouteCatalog.Describe(profile).Country);
    }
}
