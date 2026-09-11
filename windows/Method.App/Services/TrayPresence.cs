// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Method.App.ViewModels;
using Method.Core.Storage;

namespace Method.App.Services;

/// <summary>
/// Значок в трее.
///
/// <para>Клиент такого рода живёт свёрнутым: открывают его раз в день, чтобы
/// нажать одну кнопку. Значок здесь не украшение, а основной способ им
/// пользоваться — состояние видно, не открывая окна, и переключить можно оттуда же.</para>
///
/// <para>Закрытие окна прячет его, а не выходит: туннель поднимает
/// привилегированная часть и живёт он отдельно, поэтому «закрыл окно» и
/// «отключился» — разные действия. Выход из клиента — отдельный пункт меню, и он
/// тоже туннель не гасит: человек мог включить автозапуск и рассчитывать, что
/// защита останется.</para>
/// </summary>
public sealed class TrayPresence : IDisposable
{
    private readonly MainViewModel _model;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _toggleItem;
    // Значки меняются вместе с оформлением, поэтому не только для чтения.
    private WindowIcon _iconOn;
    private WindowIcon _iconOff;

    private Window? _window;

    public TrayPresence(MainViewModel model)
    {
        _model = model;

        (_iconOn, _iconOff) = Icons(SkinManager.Current);

        _toggleItem = new NativeMenuItem("Подключить");
        _toggleItem.Click += (_, _) => _model.ToggleCommand.Execute(null);

        var openItem = new NativeMenuItem("Открыть Method");
        openItem.Click += (_, _) => ShowWindow();

        var quitItem = new NativeMenuItem("Выйти");
        quitItem.Click += (_, _) => Quit();

        _icon = new TrayIcon
        {
            Icon = _iconOff,
            ToolTipText = "Method — не подключено",
            IsVisible = true,
            Menu = [openItem, _toggleItem, new NativeMenuItemSeparator(), quitItem],
        };

        _icon.Clicked += (_, _) => ShowWindow();

        _model.PropertyChanged += OnModelChanged;
        SkinManager.Changed += OnSkinChanged;
        Sync();
    }

    /// <summary>Привязать окно: прятать вместо закрытия и показывать по значку.</summary>
    public void Attach(Window window)
    {
        _window = window;
        window.Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Настоящий выход идёт через пункт меню и через завершение работы системы —
        // их отменять нельзя, иначе окно не закроется никогда.
        if (_exiting || e.CloseReason != WindowCloseReason.WindowClosing) return;

        e.Cancel = true;
        _window?.Hide();
    }

    private bool _exiting;

    private void ShowWindow()
    {
        if (_window is null) return;

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _exiting = true;

        // Туннель не гасим: он живёт в привилегированной части, и человек мог
        // рассчитывать, что защита останется после закрытия окна. Отключение — это
        // отдельное осознанное действие.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.State) or nameof(MainViewModel.SelectedName)) Sync();
    }

    private void Sync()
    {
        var connected = _model.State == ConnectionState.Connected;

        _icon.Icon = connected ? _iconOn : _iconOff;
        _icon.ToolTipText = connected
            ? $"Method — подключено\n{_model.SelectedName}"
            : $"Method — {_model.StatusText.ToLowerInvariant()}";

        _toggleItem.Header = connected ? "Отключить" : "Подключить";
    }

    /// <summary>
    /// Значок оформления. В трее знак живёт крошечным, поэтому у плакатного
    /// набора он свой: не картуш со знамёнами, а один обод с зубцами и звезда —
    /// на шестнадцати точках от полного знака остаётся пятно.
    /// </summary>
    private static (WindowIcon On, WindowIcon Off) Icons(AppSkin skin) =>
        skin == AppSkin.Poster
            ? (Load("avares://Method/Assets/tray-poster-on.ico"),
               Load("avares://Method/Assets/tray-poster-off.ico"))
            : (Load("avares://Method/Assets/tray-on.ico"),
               Load("avares://Method/Assets/tray-off.ico"));

    private void OnSkinChanged(AppSkin skin)
    {
        (_iconOn, _iconOff) = Icons(skin);
        Sync();
    }

    private static WindowIcon Load(string uri) => new(AssetLoader.Open(new Uri(uri)));

    public void Dispose()
    {
        _model.PropertyChanged -= OnModelChanged;
        SkinManager.Changed -= OnSkinChanged;
        if (_window is not null) _window.Closing -= OnWindowClosing;
        _icon.Dispose();
    }
}
