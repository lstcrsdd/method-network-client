// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Method.Core.Models;

namespace Method.App.ViewModels;

/// <summary>
/// Правило маршрутизации в списке. Само правило — запись, менять её нельзя;
/// здесь она подменяется целиком, а интерфейс узнаёт об этом по уведомлению.
/// </summary>
public sealed partial class RuleItem : ObservableObject
{
    private readonly Action<RuleItem>? _changed;

    /// <summary>Как зовут узел полосы. Имена живут в списке серверов, не здесь.</summary>
    private Func<Guid, string?>? _nodeName;

    public RuleItem(RouteRule rule, Action<RuleItem>? changed = null)
    {
        _rule = rule;
        _changed = changed;
    }

    [ObservableProperty]
    private RouteRule _rule;

    /// <summary>Что сопоставляется — домен или диапазон адресов.</summary>
    public string Value => Rule.Value;

    public bool ToTunnel => Rule.Aim is RouteAim.Tunnel or RouteAim.Node;

    public bool Enabled => Rule.Enabled;

    /// <summary>
    /// Куда идёт трафик. Словами, а не значком: значок здесь пришлось бы
    /// объяснять. У правила со своей полосой пишем имя узла — иначе строка
    /// «через туннель» одинаково выглядела бы и у общего трафика, и у того,
    /// что уходит отдельным узлом.
    /// </summary>
    public string AimText => Rule.Aim switch
    {
        RouteAim.Direct => "мимо туннеля",
        RouteAim.Node => NodeTitle() is { } name ? $"через {name}" : "через туннель",
        _ => "через туннель",
    };

    /// <summary>Чем правило поймано. Человеку важно видеть, что домен ловит и поддомены.</summary>
    public string MatchText => Rule.Match switch
    {
        RouteMatch.Cidr => "диапазон адресов",
        RouteMatch.Process => "программа, где бы она ни лежала",
        RouteMatch.ProcessPath => "программа по этому пути",
        _ => "домен и поддомены",
    };

    /// <summary>Куда это правило можно отправить. Список свой у каждой строки: цель
    /// применяет себя сама, а значит должна знать, к какому правилу относится.</summary>
    public ObservableCollection<RuleTarget> Targets { get; } = [];

    /// <summary>
    /// Пересобрать список назначений: два постоянных и по одному на каждый узел.
    /// Зовётся, когда меняется список серверов, — узел могли добавить или удалить
    /// уже после того, как правило написали.
    /// </summary>
    public void RefreshTargets(IReadOnlyList<(Guid Id, string Name)> nodes, Func<Guid, string?> nodeName)
    {
        _nodeName = nodeName;

        Targets.Clear();
        Targets.Add(new RuleTarget("мимо туннеля", RouteAim.Direct, null, ApplyTarget));
        Targets.Add(new RuleTarget("через туннель", RouteAim.Tunnel, null, ApplyTarget));

        foreach (var node in nodes)
            Targets.Add(new RuleTarget($"через {node.Name}", RouteAim.Node, node.Id, ApplyTarget));

        OnPropertyChanged(nameof(AimText));
    }

    /// <summary>Применить выбранное назначение.</summary>
    public void ApplyTarget(RuleTarget target)
    {
        Rule = Rule with { Aim = target.Aim, Node = target.Node };

        Notify();
        _changed?.Invoke(this);
    }

    public void Flip()
    {
        Rule = Rule with
        {
            Aim = Rule.Aim == RouteAim.Direct ? RouteAim.Tunnel : RouteAim.Direct,

            // Уходя с полосы, забываем узел: иначе правило хранило бы ссылку,
            // которая ни на что не влияет, и всплыла бы при следующем повороте.
            Node = null,
        };

        Notify();
    }

    public void ToggleEnabled()
    {
        Rule = Rule with { Enabled = !Rule.Enabled };
        Notify();
    }

    private string? NodeTitle() => Rule.Node is { } id ? _nodeName?.Invoke(id) : null;

    private void Notify()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ToTunnel));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(AimText));
        OnPropertyChanged(nameof(MatchText));
    }
}
