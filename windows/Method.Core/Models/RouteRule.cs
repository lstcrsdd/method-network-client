// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;
using System.Text.Json.Serialization;

namespace Method.Core.Models;

/// <summary>Куда правило отправляет соединение.</summary>
public enum RouteAim
{
    /// <summary>Через туннель.</summary>
    Tunnel,

    /// <summary>Мимо туннеля, напрямую.</summary>
    Direct,
}

/// <summary>Чем правило сопоставляется.</summary>
public enum RouteMatch
{
    /// <summary>Доменное имя вместе с поддоменами.</summary>
    Domain,

    /// <summary>Диапазон адресов в записи CIDR.</summary>
    Cidr,
}

/// <summary>
/// Правило маршрутизации: что и куда отправлять.
///
/// <para>Тип сопоставления не спрашиваем у человека, а выводим из написанного:
/// «192.168.0.0/16» и «example.com» различимы без вопросов, а лишний выбор в
/// поле ввода — это лишний повод ошибиться.</para>
/// </summary>
public sealed record RouteRule
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Домен или диапазон адресов.</summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("aim")]
    public RouteAim Aim { get; init; } = RouteAim.Direct;

    [JsonPropertyName("match")]
    public RouteMatch Match { get; init; } = RouteMatch.Domain;

    /// <summary>Выключенное правило хранится, но в конфигурацию не попадает.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Разобрать введённое человеком. Возвращает <c>null</c>, если строка не
    /// годится ни доменом, ни диапазоном.
    /// </summary>
    public static RouteRule? TryParse(string input, RouteAim aim)
    {
        var value = (input ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (value.Length == 0) return null;

        if (LooksLikeCidr(value))
            return NormalizeCidr(value) is { } cidr
                ? new RouteRule { Value = cidr, Aim = aim, Match = RouteMatch.Cidr }
                : null;

        return LooksLikeDomain(value)
            ? new RouteRule { Value = value, Aim = aim, Match = RouteMatch.Domain }
            : null;
    }

    /// <summary>
    /// Похоже на диапазон или отдельный адрес. Двоеточие выдаёт IPv6, точки с
    /// цифрами — IPv4; на домен ни то, ни другое не похоже.
    /// </summary>
    private static bool LooksLikeCidr(string value)
    {
        if (value.Contains(':')) return true;

        var head = value.Split('/')[0];
        var parts = head.Split('.');

        return parts.Length == 4 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    /// <summary>
    /// Одиночному адресу дописываем полную маску: движок ждёт диапазон, а человек
    /// пишет «8.8.8.8» и вправе не думать про «/32».
    /// </summary>
    private static string? NormalizeCidr(string value)
    {
        var slash = value.IndexOf('/');
        var address = slash < 0 ? value : value[..slash];
        var suffix = slash < 0 ? null : value[(slash + 1)..];

        if (!System.Net.IPAddress.TryParse(address, out var parsed)) return null;

        var full = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;

        if (suffix is null) return address + "/" + full.ToString(CultureInfo.InvariantCulture);

        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var bits)) return null;
        if (bits < 0 || bits > full) return null;

        return address + "/" + bits.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Похоже на доменное имя. Проверка нарочно нестрогая: задача — отсеять явный
    /// мусор вроде пробелов и косых черт, а не спорить с человеком о том, бывает
    /// ли такая зона.
    /// </summary>
    private static bool LooksLikeDomain(string value)
    {
        if (value.Length > 253) return false;
        if (!value.Contains('.')) return false;

        foreach (var label in value.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;

            foreach (var c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        }

        return true;
    }
}
