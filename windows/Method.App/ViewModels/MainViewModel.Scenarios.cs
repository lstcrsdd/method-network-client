// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Method.Core.Models;

namespace Method.App.ViewModels;

/// <summary>
/// Сценарии: правила маршрутизации, которые человек задаёт сам.
///
/// <para>Правила попадают в конфигурацию движка при подключении, поэтому правка
/// на поднятом туннеле сама по себе ничего не меняет — об этом здесь же и
/// сказано, вместо того чтобы оставлять человека гадать, почему исключение не
/// сработало.</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Правила в порядке применения: движок берёт первое подошедшее.</summary>
    public ObservableCollection<RuleItem> Rules { get; } = [];

    [ObservableProperty]
    private string _newRuleValue = string.Empty;

    /// <summary>Куда отправит новое правило. Мимо туннеля — частый случай, он и по умолчанию.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleAimText))]
    private bool _newRuleToTunnel;

    public string NewRuleAimText => NewRuleToTunnel ? "через туннель" : "мимо туннеля";

    [RelayCommand]
    private void ToggleNewRuleAim() => NewRuleToTunnel = !NewRuleToTunnel;

    /// <summary>Что не так с введённым. Пусто — всё в порядке.</summary>
    [ObservableProperty]
    private string? _ruleError;

    /// <summary>Правила изменены, а туннель поднят: применятся при следующем подключении.</summary>
    [ObservableProperty]
    private bool _rulesPending;

    public bool HasRules => Rules.Count > 0;

    [RelayCommand]
    private void AddRule()
    {
        var aim = NewRuleToTunnel ? RouteAim.Tunnel : RouteAim.Direct;
        var rule = RouteRule.TryParse(NewRuleValue, aim);

        if (rule is null)
        {
            RuleError = "Не похоже ни на домен, ни на диапазон адресов. Примеры: example.com, 192.168.0.0/16, 8.8.8.8";
            return;
        }

        // Повтор молча заменяет прежнее: два правила на один адрес — это спор,
        // в котором побеждает верхнее, а нижнее остаётся мёртвым грузом.
        var twin = Rules.FirstOrDefault(r => r.Rule.Value == rule.Value);
        if (twin is not null) Rules.Remove(twin);

        Rules.Add(new RuleItem(rule));

        NewRuleValue = string.Empty;
        RuleError = null;

        CommitRules();
    }

    [RelayCommand]
    private void RemoveRule(RuleItem? item)
    {
        if (item is null) return;

        Rules.Remove(item);
        CommitRules();
    }

    /// <summary>Переставить правило туда-обратно между туннелем и прямым выходом.</summary>
    [RelayCommand]
    private void FlipRule(RuleItem? item)
    {
        if (item is null) return;

        item.Flip();
        CommitRules();
    }

    /// <summary>Выключенное правило остаётся в списке, но в конфигурацию не идёт.</summary>
    [RelayCommand]
    private void ToggleRule(RuleItem? item)
    {
        if (item is null) return;

        item.ToggleEnabled();
        CommitRules();
    }

    private void LoadRules(IReadOnlyList<RouteRule> saved)
    {
        Rules.Clear();
        foreach (var rule in saved) Rules.Add(new RuleItem(rule));

        OnPropertyChanged(nameof(HasRules));
    }

    /// <summary>Сохранить и, если туннель поднят, предупредить об отложенном действии.</summary>
    private void CommitRules()
    {
        OnPropertyChanged(nameof(HasRules));
        SaveSettings();

        RulesPending = State == ConnectionState.Connected;
    }
}
