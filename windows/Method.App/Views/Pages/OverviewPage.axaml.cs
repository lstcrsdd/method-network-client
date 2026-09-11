// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Method.App.Services;
using Method.Core.Storage;

namespace Method.App.Views.Pages;

/// <summary>
/// Обзор. Сама страница ничего не показывает: она подставляет ту композицию,
/// которая отвечает текущему оформлению.
///
/// <para>За сменой оформления страница не следит нарочно: при переключении окно
/// собирает своё тело заново, и страница вместе с ним создаётся с нуля. Вторая
/// подписка на то же событие означала бы два хозяина у одного дерева.</para>
/// </summary>
public partial class OverviewPage : UserControl
{
    public OverviewPage()
    {
        AvaloniaXamlLoader.Load(this);

        var host = this.FindControl<Panel>("Host")!;
        host.Children.Add(SkinManager.Current == AppSkin.Poster
            ? new OverviewPoster()
            : new OverviewPlain());
    }
}
