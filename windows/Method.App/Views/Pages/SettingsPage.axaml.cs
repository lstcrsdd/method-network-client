// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Method.App.Services;
using Method.App.ViewModels;

namespace Method.App.Views.Pages;

public partial class SettingsPage : UserControl
{
    private readonly WindowsDeviceIdentity _device = new();

    public SettingsPage()
    {
        AvaloniaXamlLoader.Load(this);

        // Версия берётся у самой сборки, а не пишется в разметке рядом: две записи
        // одного числа расходятся при первом же выпуске, и разбор обращения идёт
        // по ложному следу.
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        if (this.FindControl<TextBlock>("VersionText") is { } text) text.Text = version;

        if (this.FindControl<TextBlock>("HwidText") is { } hwid) hwid.Text = _device.Hwid;
    }

    private async void OnAddSubscription(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        var box = this.FindControl<TextBox>("SubscriptionUrl");
        var status = this.FindControl<TextBlock>("SubscriptionStatus");
        var button = sender as Button;
        if (box is null || status is null) return;

        var url = (box.Text ?? "").Trim();
        if (url.Length == 0)
        {
            Show(status, "Вставьте адрес подписки.");
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Show(status, "Адрес подписки начинается с http:// или https://.");
            return;
        }

        // Загрузка идёт по сети и занимает секунды. Без запрета повторного нажатия
        // человек нажмёт ещё раз, и та же подписка поедет загружаться дважды.
        if (button is not null) button.IsEnabled = false;
        Show(status, "Загружаю…");

        try
        {
            var error = await model.AddSubscriptionAsync(url);

            if (error is null)
            {
                box.Text = "";
                Show(status, "Подписка добавлена.");
            }
            else
            {
                Show(status, error);
            }
        }
        catch (Exception ex)
        {
            Show(status, $"Не вышло: {ex.Message}");
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private async void OnCopyHwid(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(_device.Hwid);
        }
        catch (Exception ex)
        {
            if (this.FindControl<TextBlock>("SubscriptionStatus") is { } status)
                Show(status, $"Скопировать не удалось: {ex.Message}");
        }
    }

    private static void Show(TextBlock status, string text)
    {
        status.Text = text;
        status.IsVisible = true;
    }
}
