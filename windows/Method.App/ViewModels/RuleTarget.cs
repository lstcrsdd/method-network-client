// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using CommunityToolkit.Mvvm.Input;
using Method.Core.Models;

namespace Method.App.ViewModels;

/// <summary>
/// Куда правило может отправить соединение: мимо туннеля, в туннель, через
/// названный узел или на усмотрение клиента.
///
/// <para>Цель умеет применить себя сама — ей передают, что делать при выборе.
/// Иначе в команду пришлось бы передавать сразу два значения, правило и цель, а
/// список целей живёт внутри меню, оторванного от дерева строки: второй параметр
/// туда просто неоткуда взять.</para>
/// </summary>
public sealed partial class RuleTarget(string title, RouteAim aim, Guid? node, Action<RuleTarget> apply)
{
    /// <summary>Как называется в меню: «мимо туннеля», «выберет клиент», имя узла.</summary>
    public string Title { get; } = title;

    public RouteAim Aim { get; } = aim;

    /// <summary>Узел полосы. Пусто у всех целей, кроме «через такой-то узел».</summary>
    public Guid? Node { get; } = node;

    [RelayCommand]
    private void Apply() => apply(this);
}
