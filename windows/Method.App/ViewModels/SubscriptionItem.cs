// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Method.Core.Storage;

namespace Method.App.ViewModels;

/// <summary>Подписка в списке серверов: заголовок группы и её узлы.</summary>
public sealed partial class SubscriptionItem : ObservableObject
{
    public SubscriptionItem(Subscription subscription) => _subscription = subscription;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Url))]
    [NotifyPropertyChangedFor(nameof(Announce))]
    [NotifyPropertyChangedFor(nameof(HasAnnounce))]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(HasTraffic))]
    [NotifyPropertyChangedFor(nameof(TrafficText))]
    [NotifyPropertyChangedFor(nameof(TrafficFraction))]
    [NotifyPropertyChangedFor(nameof(IsTrafficCritical))]
    [NotifyPropertyChangedFor(nameof(IsTrafficWarning))]
    [NotifyPropertyChangedFor(nameof(HasExpiry))]
    [NotifyPropertyChangedFor(nameof(ExpiryText))]
    private Subscription _subscription;

    /// <summary>Узлы этой подписки, разложенные по странам.</summary>
    public ObservableCollection<CountryGroup> Countries { get; } = [];

    /// <summary>Они же общим списком. Те же объекты, что и в общем перечне.</summary>
    public ObservableCollection<ServerItem> Servers { get; } = [];

    public Guid Id => Subscription.Id;

    /// <summary>Имя, которым подписка называет себя сама, иначе — как её назвал человек.</summary>
    public string Title => Subscription.Title ?? Subscription.Name;

    public string Url => Subscription.Url;

    public string? Announce => Subscription.Announce;

    public bool HasAnnounce => !string.IsNullOrWhiteSpace(Subscription.Announce);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private bool _isRefreshing;

    public string ServerCountText => Servers.Count switch
    {
        1 => "1 сервер",
        >= 2 and <= 4 => $"{Servers.Count} сервера",
        _ => $"{Servers.Count} серверов",
    };

    /// <summary>
    /// Строка под именем: когда обновлялась, как часто обновляется сама и что
    /// пошло не так. Отказ показывается здесь же — подписка, молча не обновившаяся
    /// неделю, выглядит рабочей ровно до того дня, когда узлы перестанут отвечать.
    /// </summary>
    public string StatusLine
    {
        get
        {
            if (IsRefreshing) return "обновляется…";

            var parts = new List<string>();

            if (Subscription.LastError is { Length: > 0 } error) parts.Add(error);
            else if (Subscription.LastUpdatedAt is { } updated) parts.Add("обновлено " + Ago(updated));
            else parts.Add("ещё не обновлялась");

            if (Subscription.UpdateIntervalHours is { } hours and > 0)
                parts.Add($"автообновление {hours} ч");

            if (Subscription.LastSkippedCount > 0)
                parts.Add($"не разобрано ссылок: {Subscription.LastSkippedCount}");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Последнее обновление кончилось отказом. Цвет назначит стиль.</summary>
    public bool HasError => Subscription.LastError is { Length: > 0 };

    // MARK: Трафик и срок

    /// <summary>Провайдер прислал остаток трафика.</summary>
    public bool HasTraffic => Subscription.TrafficTotal is > 0;

    public string TrafficText =>
        Subscription.TrafficTotal is { } total and > 0
            ? $"{Gigabytes(Subscription.TrafficUsed ?? 0)} из {Gigabytes(total)} ГБ"
            : "";

    /// <summary>Доля израсходованного — для полоски.</summary>
    public double TrafficFraction =>
        Subscription.TrafficTotal is { } total and > 0
            ? Math.Clamp((Subscription.TrafficUsed ?? 0) / (double)total, 0, 1)
            : 0;

    /// <summary>
    /// Полоска краснеет к концу, а не сразу: до девяти десятых расхода тревожить
    /// человека нечем, а вот последняя десятая — уже повод.
    /// </summary>
    public bool IsTrafficCritical => TrafficFraction >= CriticalFraction;

    public bool IsTrafficWarning =>
        TrafficFraction >= WarningFraction && TrafficFraction < CriticalFraction;

    private const double WarningFraction = 0.75;
    private const double CriticalFraction = 0.9;

    public bool HasExpiry => Subscription.ExpiresAt is not null;

    public string ExpiryText
    {
        get
        {
            if (Subscription.ExpiresAt is not { } expires) return "";

            var left = expires - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero) return "срок истёк";

            return left.TotalDays < 30
                ? $"осталось {(int)left.TotalDays} дн"
                : $"до {expires:dd.MM.yyyy}";
        }
    }

    private static string Gigabytes(long bytes) =>
        (bytes / 1_073_741_824.0).ToString("F1", System.Globalization.CultureInfo.CurrentCulture);

    public void NotifyServersChanged() => OnPropertyChanged(nameof(ServerCountText));

    /// <summary>«2 минуты назад» вместо отметки времени: человеку важна давность, а не дата.</summary>
    private static string Ago(DateTimeOffset moment)
    {
        var span = DateTimeOffset.Now - moment;

        if (span < TimeSpan.FromSeconds(10)) return "только что";
        if (span < TimeSpan.FromMinutes(1)) return $"{(int)span.TotalSeconds} с назад";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} мин назад";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} ч назад";
        return $"{(int)span.TotalDays} дн назад";
    }
}
