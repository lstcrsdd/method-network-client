// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Method.Core.Models;
using Method.Core.Storage;

namespace Method.App.ViewModels;

/// <summary>Строка списка серверов.</summary>
public sealed partial class ServerItem(StoredProfile stored) : ObservableObject
{
    public StoredProfile Stored { get; } = stored;

    public ServerProfile Profile => Stored.Profile;

    public string Name => Profile.Name;

    /// <summary>Адрес и протокол — по ним человек отличает два узла с одинаковым именем.</summary>
    public string Subtitle => $"{Profile.Host} · {Profile.Protocol.DisplayName()}";

    /// <summary>Способ пройти. Именно он, а не страна, определяет, встанет ли соединение.</summary>
    public string Transport => Profile.Parameters.Kind switch
    {
        VpnProtocol.Hysteria2 => "QUIC",
        VpnProtocol.VlessReality =>
            string.IsNullOrEmpty(Profile.Parameters.VlessReality!.GrpcServiceName) ? "Reality" : "Reality gRPC",
        VpnProtocol.Trojan =>
            !string.IsNullOrEmpty(Profile.Parameters.Trojan!.GrpcServiceName) ? "gRPC"
            : !string.IsNullOrEmpty(Profile.Parameters.Trojan!.WsPath) ? "WebSocket"
            : "TLS",
        VpnProtocol.Shadowsocks => "SS",
        _ => "",
    };

    /// <summary>Метка выделения, которую поставил провайдер. Пусто — обычный сервер.</summary>
    public string? FeaturedLabel => Profile.FeaturedLabel;

    public bool IsFeatured => Profile.IsFeatured;

    [ObservableProperty]
    private bool _isSelected;

    // MARK: Задержка

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingText))]
    [NotifyPropertyChangedFor(nameof(PingBrush))]
    [NotifyPropertyChangedFor(nameof(HasPing))]
    private int? _ping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingText))]
    [NotifyPropertyChangedFor(nameof(HasPing))]
    private bool _isMeasuring;

    public bool HasPing => IsMeasuring || Ping is not null || _measured;

    private bool _measured;

    public string PingText
    {
        get
        {
            if (IsMeasuring) return "…";
            if (Ping is { } ms) return $"{ms} мс";
            return _measured ? "нет ответа" : "";
        }
    }

    /// <summary>
    /// Цвет задержки. Пороги те же, что на других платформах: до восьмидесяти —
    /// хорошо, до ста шестидесяти — терпимо, дальше — плохо.
    /// </summary>
    public IBrush PingBrush => Ping switch
    {
        null => MutedBrush,
        < 80 => GoodBrush,
        < 160 => FairBrush,
        _ => PoorBrush,
    };

    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#29C940"));
    private static readonly IBrush FairBrush = new SolidColorBrush(Color.Parse("#FFBD2E"));
    private static readonly IBrush PoorBrush = new SolidColorBrush(Color.Parse("#FF5E57"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#FFFFFF"), 0.28);

    /// <summary>Отметить, что замер был. До первого замера пустое поле честнее прочерка.</summary>
    public void MarkMeasured()
    {
        _measured = true;
        OnPropertyChanged(nameof(HasPing));
        OnPropertyChanged(nameof(PingText));
    }
}
