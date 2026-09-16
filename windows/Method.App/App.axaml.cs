// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Method.App.Services;
using Method.App.ViewModels;
using Method.App.Views;

namespace Method.App;

public partial class App : Application
{
    private TrayPresence? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Модель одна на окно и на значок в трее: два состояния одного и того
            // же туннеля разъехались бы при первом же переключении из меню.
            var model = new MainViewModel();
            var window = new MainWindow(model);

            _tray = new TrayPresence(model);
            _tray.Attach(window);

            // Повторный запуск клиента поднимает это окно, а не второе.
            SingleInstance.OnSecondLaunch(() => Dispatcher.UIThread.Post(() =>
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }));

            desktop.MainWindow = window;

            // Закрытое окно прячется, а не завершает работу — выход идёт из меню
            // значка. Без этого клиент исчезал бы вместе с последним окном, а
            // человек ждал бы его в трее.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => _tray?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
