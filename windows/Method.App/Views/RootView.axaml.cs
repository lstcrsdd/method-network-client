// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Method.App.Views;

/// <summary>Тело окна: боковая панель и разделы. Всё содержимое — в разметке.</summary>
public partial class RootView : UserControl
{
    public RootView() => AvaloniaXamlLoader.Load(this);
}
