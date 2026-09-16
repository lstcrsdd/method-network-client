// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Method.App.Services;
using Method.App.ViewModels;
using Method.Core.Storage;

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

        // Удаление спрашивает подтверждение у окна: модель про окна не знает, а
        // спросить обязана — иначе мисклик по корзине стоит человеку подписки.
        model.ConfirmAsync = (heading, message, confirm) => ConfirmWindow.AskAsync(this, heading, message, confirm);

        Rebuild(SkinManager.Current);
        SkinManager.Changed += Rebuild;
        Closed += (_, _) => SkinManager.Changed -= Rebuild;

        ShowBootScreen();
    }

    /// <summary>
    /// Собрать тело окна заново.
    ///
    /// <para>Перекрасить на месте не выходит: снятый набор стилей оставляет за
    /// собой то, что успел применить внутри шаблонов элементов. Avalonia эти
    /// значения не пересчитывает, и после возврата к основному оформлению
    /// выбранный раздел оставался залит красным посреди зелёного. Дерево,
    /// построенное заново, берёт только те стили, что действуют сейчас.</para>
    ///
    /// <para>Терять при этом нечего: всё состояние живёт в модели, а она одна на
    /// оба оформления и пересборку не замечает.</para>
    /// </summary>
    private void Rebuild(AppSkin skin)
    {
        var body = this.FindControl<Panel>("Body")!;

        body.Children.Clear();
        body.Children.Add(new RootView());
    }

    /// <summary>
    /// Показать заставку запуска.
    ///
    /// <para>Только в плакатном оформлении: в основном интерфейс повторяет
    /// клиенты macOS и Android, а там такого экрана нет и появиться он не может
    /// без того, чтобы платформы разъехались.</para>
    ///
    /// <para>Только при создании окна, то есть при холодном пуске. Окно живёт всё
    /// время работы клиента и из трея не создаётся заново, а показывается, —
    /// поэтому заставка не встретит человека второй раз за сеанс.</para>
    /// </summary>
    private void ShowBootScreen()
    {
        if (SkinManager.Current != AppSkin.Poster) return;

        var body = this.FindControl<Panel>("Body")!;
        var screen = new BootScreenPoster
        {
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }],
        };

        screen.Dismissed += () =>
        {
            // Гасим прозрачностью, а убираем следом: снятый в тот же кадр экран
            // исчезал бы рывком, и вся выдержка заставки пропадала бы в последнюю
            // четверть секунды.
            screen.Opacity = 0;
            DispatcherTimer.RunOnce(() => body.Children.Remove(screen), TimeSpan.FromMilliseconds(240));
        };

        body.Children.Add(screen);
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
