// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json.Serialization;
using Method.Core.Models;

namespace Method.Core.Storage;

/// <summary>
/// Профиль сервера вместе с привязкой к подписке. Привязка нужна для двух вещей:
/// сгруппировать узлы одной подписки в списке и заменить их все разом при её
/// обновлении. Пусто — узел добавлен ссылкой вручную и обновлением не тронется.
/// </summary>
public sealed record StoredProfile
{
    [JsonPropertyName("profile")]
    public required ServerProfile Profile { get; init; }

    [JsonPropertyName("subscriptionID")]
    public Guid? SubscriptionId { get; init; }

    [JsonIgnore]
    public Guid Id => Profile.Id;
}

/// <summary>
/// Подписка — адрес, отдающий список серверов одним файлом (обычно base64 из
/// share-ссылок). Живёт только у клиента: в общую модель профиля не идёт, чтобы
/// не связывать ею другие платформы.
/// </summary>
public sealed record Subscription
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>Как подписку назвал человек.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("addedAt")]
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("lastUpdatedAt")]
    public DateTimeOffset? LastUpdatedAt { get; init; }

    /// <summary>
    /// Какой версией клиента разобраны узлы этой подписки. Разбор меняется между
    /// версиями — однажды из-за этого узлы Hysteria2 перестали подниматься, — а
    /// сохранённые узлы остаются в старом разборе, пока подписку не обновят. Не
    /// совпало с текущей версией — подписка обновляется при запуске сразу, а не
    /// по расписанию через часы.
    /// </summary>
    [JsonPropertyName("parsedBy")]
    public string? ParsedBy { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }

    /// <summary>
    /// Сколько ссылок из последнего обновления не удалось разобрать. Показывается
    /// числом: подписка, которая молча привезла на три узла меньше, обнаруживается
    /// только на попытке подключиться к пропавшему.
    /// </summary>
    [JsonPropertyName("lastSkippedCount")]
    public int LastSkippedCount { get; init; }

    /// <summary>Как подписка называет себя сама. Человек знает её по этому имени, а не по домену.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Строка от провайдера.</summary>
    [JsonPropertyName("announce")]
    public string? Announce { get; init; }

    /// <summary>Часы между обновлениями, как их просит сама подписка.</summary>
    [JsonPropertyName("updateIntervalHours")]
    public int? UpdateIntervalHours { get; init; }

    [JsonPropertyName("webPageURL")]
    public string? WebPageUrl { get; init; }

    [JsonPropertyName("supportURL")]
    public string? SupportUrl { get; init; }

    // Заполняется из заголовка с данными о трафике, если сервер его присылает.

    [JsonPropertyName("trafficUsed")]
    public long? TrafficUsed { get; init; }

    [JsonPropertyName("trafficTotal")]
    public long? TrafficTotal { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}
