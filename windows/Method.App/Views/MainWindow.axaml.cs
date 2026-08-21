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
using Method.App.ViewModels;

namespace Method.App.Views;

public partial class MainWindow : Window
{
    // Нужен сборщику превью Avalonia; в работе окно создаётся с готовой моделью.
    public MainWindow() : this(new MainViewModel())
    {
    }

    public MainWindow(MainViewModel model)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = model;
    }

    /// <summary>
    /// Своя полоса заголовка означает и своё перетаскивание: системной рамки у окна
    /// нет, и без этого его нельзя было бы сдвинуть.
    /// </summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // Двойной щелчок по полосе разворачивает окно — так ведёт себя любое окно
        // Windows, и своя рамка не повод отучать от привычки.
        if (e.ClickCount == 2) ToggleMaximized();
        else BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
