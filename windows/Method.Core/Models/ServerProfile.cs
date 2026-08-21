// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json.Serialization;

namespace Method.Core.Models;

/// <summary>
/// Профиль одного сервера. Параметры протокола лежат отдельным объектом, поэтому
/// новый протокол не требует новой модели.
/// </summary>
public sealed record ServerProfile
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Протокол. Свойство вычисляемое, а не хранимое: источник правды — параметры.
    /// Хранимое поле рано или поздно разошлось бы с ними, а профиль, у которого
    /// протокол говорит одно, а параметры другое, не падает — он подключается не туда.
    /// В JSON поле пишется (его ждут macOS и Android), при чтении берётся из параметров.
    /// </summary>
    [JsonPropertyName("protocol")]
    public VpnProtocol Protocol => Parameters.Kind;

    [JsonPropertyName("host")]
    public required string Host { get; init; }

    [JsonPropertyName("port")]
    public required int Port { get; init; }

    [JsonPropertyName("parameters")]
    public required ServerParameters Parameters { get; init; }

    /// <summary>
    /// Метка выделения, которую провайдер поставил в имя узла: «VIP», «PREMIUM», «⭐».
    /// Пусто — обычный сервер.
    ///
    /// Читается именно из имени, потому что имя записи (фрагмент после «#») —
    /// единственный канал, который провайдер задаёт свободно поузлово в любой
    /// панели без её доработки. Заголовка «выдели этот узел» не шлёт никто.
    /// </summary>
    [JsonPropertyName("featuredLabel")]
    public string? FeaturedLabel { get; init; }

    [JsonIgnore]
    public bool IsFeatured => FeaturedLabel is not null;
}
