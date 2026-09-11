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

    /// <summary>
    /// Куда отправит новое правило. Мимо туннеля — частый случай, он и по
    /// умолчанию. Узел выбирается здесь же, а не назначается потом уже
    /// созданному правилу: дописывать назначение вдогонку — значит исправлять
    /// то, что только что сделал.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleAimText))]
    private RouteAim _newRuleAim = RouteAim.Direct;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleAimText))]
    private Guid? _newRuleNode;

    public string NewRuleAimText => NewRuleAim switch
    {
        RouteAim.Direct => "мимо туннеля",
        RouteAim.Node => NewRuleNode is { } id && NodeName(id) is { } name ? $"через {name}" : "через туннель",
        _ => "через туннель",
    };

    /// <summary>Назначения для новой строки — те же, что и у готовых правил.</summary>
    public ObservableCollection<RuleTarget> NewRuleTargets { get; } = [];

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
        var rule = RouteRule.TryParse(NewRuleValue, NewRuleAim);

        if (rule is null)
        {
            RuleError = "Не похоже ни на домен, ни на диапазон адресов. Примеры: example.com, 192.168.0.0/16, 8.8.8.8";
            return;
        }

        // Повтор молча заменяет прежнее: два правила на один адрес — это спор,
        // в котором побеждает верхнее, а нижнее остаётся мёртвым грузом.
        var twin = Rules.FirstOrDefault(r => r.Rule.Value == rule.Value);
        if (twin is not null) Rules.Remove(twin);

        // Узел прикладываем к разобранному правилу: разбор занят тем, что́
        // ловить, а не тем, куда отправлять.
        Rules.Add(Track(rule with { Node = NewRuleAim == RouteAim.Node ? NewRuleNode : null }));

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

        // Через Track, а не напрямую: строка, созданная без подписки на
        // сохранение, меняется на экране и не доезжает до диска — выбор узла
        // живёт до перезапуска и молча пропадает.
        foreach (var rule in saved) Rules.Add(Track(rule));

        OnPropertyChanged(nameof(HasRules));
    }

    /// <summary>
    /// Обернуть правило в строку списка и подписать её на сохранение: цель
    /// назначения применяется прямо из меню, минуя команды окна, и без этого
    /// выбор узла жил бы только до перезапуска.
    /// </summary>
    private RuleItem Track(RouteRule rule) => new(rule, _ => CommitRules());

    /// <summary>
    /// Раздать строкам список узлов. Зовётся при всякой перемене в серверах:
    /// узел могли добавить или удалить уже после того, как правило написали.
    /// </summary>
    private void RefreshRuleTargets()
    {
        var nodes = Servers.Select(s => (s.Profile.Id, s.Name)).ToArray();

        foreach (var item in Rules) item.RefreshTargets(nodes, NodeName);

        NewRuleTargets.Clear();
        NewRuleTargets.Add(new RuleTarget("мимо туннеля", RouteAim.Direct, null, PickNewRuleTarget));
        NewRuleTargets.Add(new RuleTarget("через туннель", RouteAim.Tunnel, null, PickNewRuleTarget));

        foreach (var node in nodes)
            NewRuleTargets.Add(new RuleTarget($"через {node.Name}", RouteAim.Node, node.Id, PickNewRuleTarget));
    }

    private void PickNewRuleTarget(RuleTarget target)
    {
        NewRuleAim = target.Aim;
        NewRuleNode = target.Node;
    }

    /// <summary>Имя узла по опознавателю; пусто — узла больше нет.</summary>
    private string? NodeName(Guid id) =>
        Servers.FirstOrDefault(s => s.Profile.Id == id)?.Name;

    /// <summary>Сохранить и, если туннель поднят, предупредить об отложенном действии.</summary>
    private void CommitRules()
    {
        OnPropertyChanged(nameof(HasRules));
        SaveSettings();

        RulesPending = State == ConnectionState.Connected;
    }
}
