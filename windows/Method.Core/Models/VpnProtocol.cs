// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json.Serialization;
using Method.Core.Json;

namespace Method.Core.Models;

/// <summary>Транспортный протокол сервера.</summary>
[JsonConverter(typeof(VpnProtocolJsonConverter))]
public enum VpnProtocol
{
    Hysteria2,
    VlessReality,
    Trojan,
    Shadowsocks,
}

public static class VpnProtocolExtensions
{
    /// <summary>
    /// Значение, которым протокол называется в JSON. Совпадает с macOS и Android
    /// дословно: модель профиля общая на три платформы, и профиль, сохранённый
    /// одной, обязан читаться другими.
    /// </summary>
    public static string ToWireValue(this VpnProtocol p) => p switch
    {
        VpnProtocol.Hysteria2 => "hysteria2",
        VpnProtocol.VlessReality => "vless-reality",
        VpnProtocol.Trojan => "trojan",
        VpnProtocol.Shadowsocks => "shadowsocks",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, "Неизвестный протокол"),
    };

    public static string DisplayName(this VpnProtocol p) => p switch
    {
        VpnProtocol.Hysteria2 => "Hysteria2",
        VpnProtocol.VlessReality => "VLESS + Reality",
        VpnProtocol.Trojan => "Trojan",
        VpnProtocol.Shadowsocks => "Shadowsocks",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, "Неизвестный протокол"),
    };

    /// <summary>
    /// Разбор значения из JSON. Неизвестное значение — исключение, а не запасной
    /// протокол: профиль, прочитанный «как-нибудь», подключается не туда.
    /// </summary>
    public static VpnProtocol FromWireValue(string value) => value switch
    {
        "hysteria2" => VpnProtocol.Hysteria2,
        "vless-reality" => VpnProtocol.VlessReality,
        "trojan" => VpnProtocol.Trojan,
        "shadowsocks" => VpnProtocol.Shadowsocks,
        _ => throw new FormatException($"Неизвестный протокол: {value}"),
    };
}
