// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Method.App.Services;
using Method.Core.Storage;

namespace Method.App.ViewModels;

/// <summary>
/// Настройки клиента: чтение при запуске и запись при каждом изменении.
///
/// <para>Раньше их не было вовсе, и тумблеры возвращались в исходное при каждом
/// открытии. Настройка, которую надо ставить заново каждый раз, — это не
/// настройка, а вопрос.</para>
/// </summary>
public sealed partial class MainViewModel
{
    private readonly AppSettingsStore _settingsStore = AppSettingsStore.Default();
    private bool _settingsLoaded;

    /// <summary>Оформление. Отличается только видом — логика у обоих одна.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBaseSkin))]
    [NotifyPropertyChangedFor(nameof(IsPosterSkin))]
    private AppSkin _skin = AppSkin.Base;

    public bool IsBaseSkin => Skin == AppSkin.Base;

    public bool IsPosterSkin => Skin == AppSkin.Poster;

    [RelayCommand]
    private void SetSkin(string? name)
    {
        if (Enum.TryParse<AppSkin>(name, out var value)) Skin = value;
    }

    partial void OnSkinChanged(AppSkin value)
    {
        var problem = SkinManager.Apply(value);
        if (problem is not null) Detail = $"Оформление не переключилось: {problem}";

        SaveSettings();
    }

    private AppSettings LoadSettings()
    {
        var settings = _settingsStore.Load();

        KillSwitchEnabled = settings.KillSwitch;
        BlockLanDns = settings.BlockLanDns;
        AutoReconnect = settings.AutoReconnect;
        AutoRouting = settings.AutoRouting;
        Skin = settings.Skin;

        // Флаг ставится последним: до него каждое присваивание выше вызвало бы
        // запись файла, и загрузка настроек превратилась бы в их перезапись.
        _settingsLoaded = true;
        return settings;
    }

    private void SaveSettings()
    {
        if (!_settingsLoaded) return;

        _settingsStore.Save(new AppSettings
        {
            Skin = Skin,
            KillSwitch = KillSwitchEnabled,
            BlockLanDns = BlockLanDns,
            AutoReconnect = AutoReconnect,
            AutoRouting = AutoRouting,
            SelectedServer = SelectedServer?.Profile.Id,
            Rules = Rules.Select(r => r.Rule).ToArray(),
        });
    }

    partial void OnKillSwitchEnabledChanged(bool value) => SaveSettings();

    partial void OnBlockLanDnsChanged(bool value) => SaveSettings();

    partial void OnAutoReconnectChanged(bool value) => SaveSettings();

    partial void OnAutoRoutingChanged(bool value) => SaveSettings();
}
