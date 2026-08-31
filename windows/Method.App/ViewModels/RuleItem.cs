// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using CommunityToolkit.Mvvm.ComponentModel;
using Method.Core.Models;

namespace Method.App.ViewModels;

/// <summary>
/// Правило маршрутизации в списке. Само правило — запись, менять её нельзя;
/// здесь она подменяется целиком, а интерфейс узнаёт об этом по уведомлению.
/// </summary>
public sealed partial class RuleItem(RouteRule rule) : ObservableObject
{
    [ObservableProperty]
    private RouteRule _rule = rule;

    /// <summary>Что сопоставляется — домен или диапазон адресов.</summary>
    public string Value => Rule.Value;

    public bool ToTunnel => Rule.Aim == RouteAim.Tunnel;

    public bool Enabled => Rule.Enabled;

    /// <summary>Куда идёт трафик. Словами, а не значком: значок здесь пришлось бы объяснять.</summary>
    public string AimText => ToTunnel ? "через туннель" : "мимо туннеля";

    /// <summary>Чем правило поймано. Человеку важно видеть, что домен ловит и поддомены.</summary>
    public string MatchText => Rule.Match switch
    {
        RouteMatch.Cidr => "диапазон адресов",
        RouteMatch.Process => "программа, где бы она ни лежала",
        RouteMatch.ProcessPath => "программа по этому пути",
        _ => "домен и поддомены",
    };

    public void Flip()
    {
        Rule = Rule with { Aim = ToTunnel ? RouteAim.Direct : RouteAim.Tunnel };
        Notify();
    }

    public void ToggleEnabled()
    {
        Rule = Rule with { Enabled = !Rule.Enabled };
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ToTunnel));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(AimText));
        OnPropertyChanged(nameof(MatchText));
    }
}
