// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Method.Core.Models;
using Method.Core.Storage;
using Method.Core.Subscriptions;

namespace Method.App.ViewModels;

/// <summary>
/// Подписки: загрузка по адресу, обновление и группировка узлов.
///
/// <para>Узел, приехавший из подписки, привязан к ней. При обновлении узлы этой
/// подписки заменяются целиком — иначе удалённые провайдером серверы оставались бы
/// в списке навсегда и выглядели рабочими.</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Группы: подписка и её узлы.</summary>
    public ObservableCollection<SubscriptionItem> Subscriptions { get; } = [];

    /// <summary>Узлы, добавленные ссылкой вручную. Обновлением они не трогаются.</summary>
    public ObservableCollection<ServerItem> LooseServers { get; } = [];

    /// <summary>Они же, разложенные по странам.</summary>
    public ObservableCollection<CountryGroup> LooseCountries { get; } = [];

    public bool HasSubscriptions => Subscriptions.Count > 0;

    public bool HasLooseServers => LooseServers.Count > 0;

    /// <summary>
    /// Разобрать вставленный текст: адреса http и https — подписки, остальное —
    /// ссылки на узлы. Разделять их за человека правильнее, чем требовать от него
    /// знать, куда что вставлять: он копирует то, что дал провайдер.
    /// </summary>
    public async Task<ImportResult> AddFromTextAsync(string text)
    {
        var lines = (text ?? "").Split('\n');
        var subscriptionUrls = new List<string>();
        var linkLines = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                subscriptionUrls.Add(line);
            else
                linkLines.Add(line);
        }

        var result = AddLinks(string.Join("\n", linkLines));

        var added = result.Added;
        var failures = new List<string>();

        foreach (var url in subscriptionUrls)
        {
            var before = Servers.Count;
            var error = await AddSubscriptionAsync(url).ConfigureAwait(true);

            if (error is not null) failures.Add(error);
            else added += Servers.Count - before;
        }

        return result with { Added = added, SubscriptionErrors = failures };
    }

    /// <summary>Добавить подписку. Пусто — вышло; иначе текст отказа.</summary>
    public async Task<string?> AddSubscriptionAsync(string url)
    {
        if (Subscriptions.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
            return "Такая подписка уже добавлена.";

        var item = new SubscriptionItem(new Subscription { Url = url, Name = HostOf(url) });
        var error = await RefreshAsync(item).ConfigureAwait(true);

        // Не приехало ничего — не заводим запись: пустая подписка в списке выглядит
        // рабочей и молчит о причине.
        if (error is not null && item.Servers.Count == 0)
        {
            RemoveServersOf(item.Id);
            return error;
        }

        Subscriptions.Add(item);
        SaveAll();
        NotifyGroupsChanged();
        return error;
    }

    [RelayCommand]
    private async Task RefreshSubscriptionAsync(SubscriptionItem? item)
    {
        if (item is null) return;

        await RefreshAsync(item).ConfigureAwait(true);
        SaveAll();
        NotifyGroupsChanged();
    }

    [RelayCommand]
    private async Task RefreshAllSubscriptionsAsync()
    {
        foreach (var item in Subscriptions.ToArray())
            await RefreshAsync(item).ConfigureAwait(true);

        SaveAll();
        NotifyGroupsChanged();
    }

    [RelayCommand]
    private void RemoveSubscription(SubscriptionItem? item)
    {
        if (item is null) return;

        RemoveServersOf(item.Id);
        Subscriptions.Remove(item);

        SaveAll();
        NotifyGroupsChanged();
    }

    /// <summary>
    /// Загрузить подписку заново и заменить её узлы. Возвращает текст отказа или пусто.
    /// </summary>
    private async Task<string?> RefreshAsync(SubscriptionItem item)
    {
        item.IsRefreshing = true;

        try
        {
            var result = await _subscriptions.FetchAsync(item.Url).ConfigureAwait(true);

            // Что было выбрано — то и останется выбранным: обновление подписки не
            // повод молча переключить человека на другой сервер.
            var selected = SelectedServer?.Profile;

            RemoveServersOf(item.Id);

            foreach (var profile in result.Profiles)
                Servers.Add(new ServerItem(new StoredProfile { Profile = profile, SubscriptionId = item.Id }));

            item.Subscription = item.Subscription with
            {
                Title = result.Title ?? item.Subscription.Title,
                Name = result.SuggestedName ?? item.Subscription.Name,
                Announce = result.Announce,
                UpdateIntervalHours = result.UpdateIntervalHours,
                LastSkippedCount = result.SkippedCount,
                LastUpdatedAt = DateTimeOffset.Now,
                LastError = null,
                TrafficUsed = result.TrafficUsed,
                TrafficTotal = result.TrafficTotal,
                ExpiresAt = result.ExpiresAt,
            };

            RestoreSelection(selected);
            return null;
        }
        catch (SubscriptionException e)
        {
            // Отказ запоминается на самой подписке и виден в списке. Прежние узлы
            // остаются: сеть могла отвалиться на минуту, а выкинуть из-за этого все
            // серверы значит оставить человека без связи ровно тогда, когда она нужнее.
            item.Subscription = item.Subscription with { LastError = e.Message };
            return e.Message;
        }
        catch (Exception e)
        {
            item.Subscription = item.Subscription with { LastError = e.Message };
            return e.Message;
        }
        finally
        {
            item.IsRefreshing = false;
            RebuildGroups();
        }
    }

    private void RemoveServersOf(Guid subscriptionId)
    {
        foreach (var item in Servers.Where(s => s.Stored.SubscriptionId == subscriptionId).ToArray())
            Servers.Remove(item);
    }

    /// <summary>Вернуть выбор на тот же узел после замены списка.</summary>
    private void RestoreSelection(ServerProfile? previous)
    {
        if (previous is null)
        {
            SelectedServer ??= Servers.FirstOrDefault();
            return;
        }

        SelectedServer = Servers.FirstOrDefault(s => IsSameNode(s.Profile, previous))
                         ?? SelectedServer
                         ?? Servers.FirstOrDefault();
    }

    /// <summary>Разложить общий список узлов по подпискам, а внутри — по странам.</summary>
    private void RebuildGroups()
    {
        foreach (var group in Subscriptions)
        {
            group.Servers.Clear();
            foreach (var server in Servers.Where(s => s.Stored.SubscriptionId == group.Id))
                group.Servers.Add(server);

            Regroup(group.Countries, group.Servers);
            group.NotifyServersChanged();
        }

        LooseServers.Clear();
        foreach (var server in Servers.Where(s => s.Stored.SubscriptionId is null))
            LooseServers.Add(server);

        Regroup(LooseCountries, LooseServers);
    }

    /// <summary>
    /// Собрать узлы по странам. Внутри страны порядок по имени, а не по тому, в
    /// каком его прислал провайдер: у него он по протоколам, и два соседних узла
    /// одного места оказывались в разных концах списка.
    /// </summary>
    private static void Regroup(ObservableCollection<CountryGroup> target, IEnumerable<ServerItem> servers)
    {
        var groups = servers
            .GroupBy(s => s.CountryCode)
            .Select(g =>
            {
                var group = new CountryGroup(g.Key, g.First().CountryName);
                foreach (var server in g.OrderBy(s => s.Name, StringComparer.CurrentCulture))
                    group.Servers.Add(server);
                return group;
            })
            .OrderBy(g => g.SortKey, StringComparer.CurrentCulture)
            .ToList();

        target.Clear();
        foreach (var group in groups) target.Add(group);
    }

    private void NotifyGroupsChanged()
    {
        OnPropertyChanged(nameof(HasSubscriptions));
        OnPropertyChanged(nameof(HasLooseServers));
        NotifyListChanged();
    }

    private void SaveAll()
    {
        Save();
        _store.SaveSubscriptions(Subscriptions.Select(s => s.Subscription));
    }

    /// <summary>Имя по умолчанию — домен: до первого обновления другого имени взять неоткуда.</summary>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    // MARK: Автообновление

    /// <summary>
    /// Проверка раз в четверть часа: не пора ли обновить какую-нибудь подписку.
    /// Срок берётся у самой подписки, а если она его не назвала — сутки.
    /// </summary>
    private void StartAutoUpdate()
    {
        _autoUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _autoUpdateTimer.Tick += async (_, _) => await AutoUpdateDueAsync().ConfigureAwait(true);
        _autoUpdateTimer.Start();
    }

    private DispatcherTimer? _autoUpdateTimer;

    private async Task AutoUpdateDueAsync()
    {
        var now = DateTimeOffset.Now;
        var updated = false;

        foreach (var item in Subscriptions.ToArray())
        {
            var interval = TimeSpan.FromHours(item.Subscription.UpdateIntervalHours is { } h and > 0 ? h : 24);
            var last = item.Subscription.LastUpdatedAt;

            if (last is not null && now - last < interval) continue;

            await RefreshAsync(item).ConfigureAwait(true);
            updated = true;
        }

        if (updated)
        {
            SaveAll();
            NotifyGroupsChanged();
        }
    }
}
