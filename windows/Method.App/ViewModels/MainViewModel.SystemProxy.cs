// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Method.App.Services;

namespace Method.App.ViewModels;

/// <summary>
/// Чужой системный прокси: что видит человек и как это снять.
///
/// <para>Предупреждение живёт отдельной строкой, а не в <see cref="Detail"/>: там
/// пишут подключение, сторож и защита, и они затирали бы друг друга. А сказать
/// про прокси нужно, пока он есть, — это не событие, а состояние машины.</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Текст предупреждения или null, если говорить не о чем.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProxyWarning))]
    private string? _proxyWarning;

    public bool HasProxyWarning => ProxyWarning is not null;

    /// <summary>Что было в прошлый раз: в журнал пишем смену, а не каждый опрос.</summary>
    private string? _lastProxySeen;

    /// <summary>
    /// Перечитать прокси и решить, говорить ли о нём.
    ///
    /// <para>Мёртвый прокси — всегда: интернета нет ни у кого, туннель тут ни при
    /// чём. Живой — только при поднятом туннеле: значит, программы идут мимо нас.
    /// Живой при отключённом туннеле — это работающий чужой клиент, и он не наше
    /// дело.</para>
    /// </summary>
    internal async Task RefreshSystemProxyAsync()
    {
        var proxy = await SystemProxy.ReadAsync().ConfigureAwait(true);

        ProxyWarning = proxy switch
        {
            null => null,
            { Alive: false } =>
                $"В Windows прописан прокси {proxy.Address}, и он не отвечает. Браузеры и большинство " +
                "программ шлют запросы туда, поэтому сайты не открываются даже при работающем туннеле. " +
                "Обычно эту запись оставляет выключенный VPN-клиент.",
            _ when State == ConnectionState.Connected =>
                $"В Windows прописан прокси {proxy.Address}. Браузеры и большинство программ идут через " +
                "него — то есть через другую программу, а не через туннель Method.",
            _ => null,
        };

        var seen = proxy is null ? null : $"{proxy.Address} {(proxy.Alive ? "отвечает" : "не отвечает")}";
        if (seen == _lastProxySeen) return;

        _lastProxySeen = seen;
        if (seen is not null)
            WorkJournal.Add("Системный прокси", seen,
                proxy!.Alive ? JournalOutcome.Warning : JournalOutcome.Failed);
    }

    [RelayCommand]
    private async Task DisableSystemProxyAsync()
    {
        var error = SystemProxy.Disable();

        WorkJournal.Add("Системный прокси", error ?? "снят, программы пойдут через туннель",
            error is null ? JournalOutcome.Ok : JournalOutcome.Failed);

        if (error is not null) Detail = $"Не удалось снять системный прокси: {error}";

        await RefreshSystemProxyAsync().ConfigureAwait(true);
    }
}
