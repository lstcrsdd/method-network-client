// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Method.App.Controls;

namespace Method.App.Views;

/// <summary>Спросить «точно?» перед необратимым действием.</summary>
public partial class ConfirmWindow : Window
{
    // Нужен сборщику превью Avalonia; в работе не вызывается.
    public ConfirmWindow() : this("Подтверждение", "", "Да")
    {
    }

    public ConfirmWindow(string heading, string message, string confirm)
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Caption>("Heading")!.Text = heading;
        this.FindControl<TextBlock>("Message")!.Text = message;
        this.FindControl<Button>("ConfirmButton")!.Content = confirm;
    }

    /// <summary>
    /// Показать поверх окна и дождаться ответа. Закрытие крестиком или Escape —
    /// это «нет»: не подтверждённое явно не удаляется.
    /// </summary>
    public static async Task<bool> AskAsync(Window owner, string heading, string message, string confirm)
    {
        var dialog = new ConfirmWindow(heading, message, confirm);
        return await dialog.ShowDialog<bool>(owner);
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}
