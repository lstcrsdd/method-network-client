// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Models;

namespace Method.Core.Subscriptions;

/// <summary>Что приехало из подписки.</summary>
public sealed record SubscriptionFetchResult
{
    public required IReadOnlyList<ServerProfile> Profiles { get; init; }

    /// <summary>Как назвать подписку, если человек не назвал её сам.</summary>
    public string? SuggestedName { get; init; }

    /// <summary>
    /// Сколько ссылок разобрать не удалось, хотя подписка в целом приехала.
    /// Показывается числом: подписка, молча привезшая на три узла меньше,
    /// обнаруживается только на попытке подключиться к пропавшему.
    /// </summary>
    public int SkippedCount { get; init; }

    /// <summary>Как подписка называет себя сама. Человек знает её по этому имени, а не по домену.</summary>
    public string? Title { get; init; }

    /// <summary>Строка от провайдера: и поддержка, и предупреждение об истечении.</summary>
    public string? Announce { get; init; }

    /// <summary>Часы между обновлениями, как их просит сама подписка.</summary>
    public int? UpdateIntervalHours { get; init; }

    public string? WebPageUrl { get; init; }
    public string? SupportUrl { get; init; }

    public long? TrafficUsed { get; init; }
    public long? TrafficTotal { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public enum SubscriptionFailure
{
    Network,
    Empty,
    UnsupportedFormat,

    /// <summary>
    /// Провайдер вернул понятное сообщение вместо серверов — «оплатите подписку»,
    /// «обратитесь к администратору». Частая практика: сообщение зашито прямо в имя
    /// фиктивного узла, чтобы его увидел любой клиент.
    /// </summary>
    ProviderMessage,
}

public sealed class SubscriptionException : Exception
{
    public SubscriptionFailure Failure { get; }

    private SubscriptionException(SubscriptionFailure failure, string message) : base(message) =>
        Failure = failure;

    public static SubscriptionException Network(string what) =>
        new(SubscriptionFailure.Network, $"Не удалось загрузить подписку: {what}");

    public static SubscriptionException Empty() =>
        new(SubscriptionFailure.Empty, "Подписка пуста или ссылки не распознаны");

    public static SubscriptionException UnsupportedFormat(string format) =>
        new(SubscriptionFailure.UnsupportedFormat, $"Формат подписки ({format}) пока не поддерживается");

    public static SubscriptionException ProviderMessage(string message) =>
        new(SubscriptionFailure.ProviderMessage,
            $"Провайдер сообщает: «{message}» — обратитесь к поставщику подписки");
}

/// <summary>
/// Устойчивый идентификатор машины для панели подписок. Реализация зависит от
/// платформы, поэтому здесь только договор.
/// </summary>
public interface IDeviceIdentity
{
    /// <summary>Идентификатор в верхнем регистре.</summary>
    string Hwid { get; }

    /// <summary>Он же, укороченный: часть панелей просит именно короткий.</summary>
    string ShortHwid { get; }

    /// <summary>Хеш идентификатора — для панелей, которым не нужен сам идентификатор.</summary>
    string HashedHwid { get; }

    /// <summary>Имя машины, как его видит человек.</summary>
    string DeviceName { get; }

    /// <summary>Название платформы для заголовка.</summary>
    string Platform { get; }
}
