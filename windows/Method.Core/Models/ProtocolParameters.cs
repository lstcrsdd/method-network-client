// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json.Serialization;

namespace Method.Core.Models;

/// <summary>Параметры Hysteria2.</summary>
public sealed record Hysteria2Parameters
{
    /// <summary>Пароль аутентификации (auth).</summary>
    [JsonPropertyName("password")]
    public required string Password { get; init; }

    /// <summary>SNI для TLS (tls.server_name). Пусто — берётся host.</summary>
    [JsonPropertyName("sni")]
    public string? Sni { get; init; }

    /// <summary>Не проверять сертификат (самоподписанные серверы).</summary>
    [JsonPropertyName("allowInsecure")]
    public bool AllowInsecure { get; init; }

    /// <summary>Salamander-обфускация: пароль. Пусто — обфускации нет.</summary>
    [JsonPropertyName("obfsPassword")]
    public string? ObfsPassword { get; init; }

    /// <summary>Лимиты полосы, Мбит/с. Пусто — без ограничения BBR.</summary>
    [JsonPropertyName("upMbps")]
    public int? UpMbps { get; init; }

    [JsonPropertyName("downMbps")]
    public int? DownMbps { get; init; }

    [JsonPropertyName("alpn")]
    public IReadOnlyList<string> Alpn { get; init; } = new[] { "h3" };

    /// <summary>
    /// Диапазон портов для прыжков, например «36000:50000». Пусто — ходить только
    /// на основной порт. Диапазон обязан не пересекаться с полосами, отданными на
    /// узле другим инстансам: правила переадресации проверяются по порядку, и
    /// попадание в чужую полосу уводит трафик на инстанс с другим паролем обфускации —
    /// связь молча умирает до следующего прыжка.
    /// </summary>
    [JsonPropertyName("serverPorts")]
    public string? ServerPorts { get; init; }

    /// <summary>Как часто менять порт. Пусто — значение sing-box по умолчанию.</summary>
    [JsonPropertyName("hopInterval")]
    public string? HopInterval { get; init; }

    // Запись сравнивается по значению, но список ALPN синтезированное сравнение
    // сверяет по ссылке — два одинаковых профиля из разных разборов оказались бы
    // разными. Поэтому равенство дописано руками.
    public bool Equals(Hysteria2Parameters? other) =>
        other is not null
        && Password == other.Password
        && Sni == other.Sni
        && AllowInsecure == other.AllowInsecure
        && ObfsPassword == other.ObfsPassword
        && UpMbps == other.UpMbps
        && DownMbps == other.DownMbps
        && ServerPorts == other.ServerPorts
        && HopInterval == other.HopInterval
        && Alpn.SequenceEqual(other.Alpn);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Password);
        hash.Add(Sni);
        hash.Add(AllowInsecure);
        hash.Add(ObfsPassword);
        hash.Add(UpMbps);
        hash.Add(DownMbps);
        hash.Add(ServerPorts);
        hash.Add(HopInterval);
        foreach (var a in Alpn) hash.Add(a);
        return hash.ToHashCode();
    }
}

/// <summary>Параметры VLESS + Reality.</summary>
public sealed record VlessRealityParameters
{
    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    /// <summary>flow, обычно «xtls-rprx-vision». Пусто — без flow.</summary>
    [JsonPropertyName("flow")]
    public string? Flow { get; init; }

    /// <summary>Маскируемый домен (server_name).</summary>
    [JsonPropertyName("sni")]
    public required string Sni { get; init; }

    /// <summary>Публичный ключ Reality (pbk).</summary>
    [JsonPropertyName("publicKey")]
    public required string PublicKey { get; init; }

    /// <summary>short_id (sid). Может быть пустым.</summary>
    [JsonPropertyName("shortID")]
    public string ShortId { get; init; } = "";

    /// <summary>Отпечаток uTLS (fp), например «chrome».</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = "chrome";

    /// <summary>
    /// Имя gRPC-сервиса. Заполнено — транспорт gRPC, пусто — голый TCP с Vision.
    /// gRPC нужен потому, что по замерам он проходит там, где Vision не проходит:
    /// у него фрейминг HTTP/2, пакеты мельче и форма трафика непохожа на голый TLS.
    /// Vision при этом быстрее там, где сеть не мешает, — поэтому нужны оба.
    /// </summary>
    [JsonPropertyName("grpcServiceName")]
    public string? GrpcServiceName { get; init; }
}

/// <summary>Параметры Trojan.</summary>
public sealed record TrojanParameters
{
    /// <summary>Пароль. В Trojan он же и аутентификация — отдельного пользователя нет.</summary>
    [JsonPropertyName("password")]
    public required string Password { get; init; }

    /// <summary>
    /// SNI. Для Trojan он обязан совпадать с именем в сертификате: протокол
    /// держится на подлинности TLS, а не на подделке, как Reality. Несовпадение —
    /// соединение не встанет.
    /// </summary>
    [JsonPropertyName("sni")]
    public string? Sni { get; init; }

    [JsonPropertyName("allowInsecure")]
    public bool AllowInsecure { get; init; }

    /// <summary>Отпечаток uTLS. Пусто — обычный стек TLS.</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    /// <summary>Имя gRPC-сервиса, если транспорт gRPC. Пусто — голый TCP.</summary>
    [JsonPropertyName("grpcServiceName")]
    public string? GrpcServiceName { get; init; }

    /// <summary>Путь WebSocket, если транспорт ws.</summary>
    [JsonPropertyName("wsPath")]
    public string? WsPath { get; init; }

    /// <summary>Заголовок Host для WebSocket.</summary>
    [JsonPropertyName("wsHost")]
    public string? WsHost { get; init; }
}

/// <summary>Параметры Shadowsocks.</summary>
public sealed record ShadowsocksParameters
{
    /// <summary>
    /// Шифр, например «2022-blake3-aes-128-gcm» или «aes-256-gcm». Передаётся ядру
    /// как есть: список поддерживаемых у sing-box свой, и подменять его догадками нельзя.
    /// </summary>
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>
    /// Пароль. Для методов семейства 2022 это base64-ключ нужной длины, и портить
    /// его нормализацией нельзя.
    /// </summary>
    [JsonPropertyName("password")]
    public required string Password { get; init; }
}
