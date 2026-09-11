// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Models;
using Method.Core.Native;

namespace Method.Core.Core;

/// <summary>
/// Перевод узла из нашего каталога в маршрут ядра.
///
/// <para>Ядро мыслит не узлами, а маршрутами: узел вместе с транспортом. У нас
/// это совпадает — один профиль и есть один маршрут, потому что «Литва по
/// Hysteria2» и «Литва по gRPC» лежат в каталоге отдельными записями. Совпадение
/// удобное, но не само собой разумеющееся, и держится оно на этом файле.</para>
///
/// <para>Главное здесь — <b>ось обхода</b>, и понимают её чаще всего неверно.
/// Ось — не протокол, а способ выглядеть в сети. Два маршрута на одной оси
/// запасными друг другу не являются: сеть, режущая QUIC, одинаково уронит и
/// Hysteria2, и TUIC, сколько их ни перебирай. Поэтому ядро и требует у полосы
/// маршруты <b>разных</b> осей — иначе «запасной путь» существует только на
/// бумаге.</para>
/// </summary>
public static class RouteCatalog
{
    /// <summary>Маршрут ядра для узла.</summary>
    public static CoreRoute Describe(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new CoreRoute(
            // Опознаватель профиля, а не тег исходящего. Тег «proxy» означает
            // «выбранный сейчас узел» и от подключения к подключению указывает
            // на разные узлы — накопленная ядром история по такому ключу
            // смешала бы замеры разных серверов в одну кучу.
            Id: profile.Id.ToString("N"),
            Node: Node(profile),
            Transport: Transport(profile),
            Country: CountryDetector.Detect(profile.Name).Code ?? string.Empty,
            Axis: Axis(profile));
    }

    /// <summary>
    /// Узел — то, что физически одно и то же железо. Берём адрес: имя приходит
    /// из подписки и у одного сервера может быть разным в разных строках, а
    /// ядру важно понимать, что два маршрута ведут в одну точку отказа.
    /// </summary>
    private static string Node(ServerProfile profile) => profile.Host;

    /// <summary>Транспорт — для человека в объяснениях, ядро сравнивает его как строку.</summary>
    public static string Transport(ServerProfile profile) => profile.Parameters.Kind switch
    {
        VpnProtocol.Hysteria2 => "hysteria2",

        VpnProtocol.VlessReality when Grpc(profile.Parameters.VlessReality!.GrpcServiceName) => "vless-grpc",
        VpnProtocol.VlessReality => "vless-vision",

        VpnProtocol.Trojan when Grpc(profile.Parameters.Trojan!.GrpcServiceName) => "trojan-grpc",
        VpnProtocol.Trojan when !string.IsNullOrEmpty(profile.Parameters.Trojan!.WsPath) => "trojan-ws",
        VpnProtocol.Trojan => "trojan-tls",

        VpnProtocol.Shadowsocks => "shadowsocks",

        _ => "неизвестный",
    };

    /// <summary>
    /// Ось обхода. Trojan во всех своих видах — одна ось: и по WebSocket, и по
    /// gRPC он остаётся настоящим TLS с подлинным сертификатом, а именно это и
    /// видит наблюдатель. Reality же разделён надвое не по прихоти: Vision идёт
    /// голым TCP, gRPC — потоком HTTP/2, и распознают их по-разному.
    /// </summary>
    public static CoreAxis Axis(ServerProfile profile) => profile.Parameters.Kind switch
    {
        VpnProtocol.Hysteria2 => CoreAxis.QuicUdp,

        VpnProtocol.VlessReality when Grpc(profile.Parameters.VlessReality!.GrpcServiceName) => CoreAxis.FakeTlsH2,
        VpnProtocol.VlessReality => CoreAxis.FakeTlsTcp,

        VpnProtocol.Trojan => CoreAxis.RealTls,
        VpnProtocol.Shadowsocks => CoreAxis.RawStream,

        // Неизвестный протокол осью не обладает: приписать ему чужую — значит
        // позволить ядру счесть его запасным путём там, где его нет.
        _ => CoreAxis.None,
    };

    private static bool Grpc(string? serviceName) => !string.IsNullOrEmpty(serviceName);
}
