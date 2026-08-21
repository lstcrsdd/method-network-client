// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Method.App.ViewModels;

namespace Method.App.Views.Pages;

public partial class ServersPage : UserControl
{
    public ServersPage() => AvaloniaXamlLoader.Load(this);

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        try
        {
            var dialog = new ImportWindow(model);

            if (TopLevel.GetTopLevel(this) is Window owner) await dialog.ShowDialog(owner);
            else dialog.Show();
        }
        catch (Exception ex)
        {
            // Окно добавления не должно ронять приложение: клиент с поднятым
            // туннелем обязан пережить любую оплошность в собственном интерфейсе.
            model.Detail = $"Окно добавления не открылось: {ex.Message}";
        }
    }
}
