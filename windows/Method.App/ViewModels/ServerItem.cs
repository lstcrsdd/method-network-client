// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using CommunityToolkit.Mvvm.ComponentModel;
using Method.Core.Models;
using Method.Core.Storage;

namespace Method.App.ViewModels;

/// <summary>Строка списка серверов.</summary>
public sealed partial class ServerItem(StoredProfile stored) : ObservableObject
{
    public StoredProfile Stored { get; } = stored;

    public ServerProfile Profile => Stored.Profile;

    /// <summary>
    /// Страна и имя без флага. Разбор делается здесь, а не в модели профиля:
    /// в файле имя должно остаться таким, каким его прислал провайдер, — его
    /// читают ещё и macOS с Android, и там флаг показывается прямо в строке.
    /// </summary>
    private CountryMatch Country => _country ??= CountryDetector.Detect(Profile.Name);

    private CountryMatch? _country;

    /// <summary>Имя без флага: рисовать его мы теперь умеем сами, и дважды не нужно.</summary>
    public string Name => Country.CleanName.Length > 0 ? Country.CleanName : Profile.Name;

    public string? CountryCode => Country.Code;

    public string? CountryName => Country.Name;

    /// <summary>Адрес и протокол — по ним человек отличает два узла с одинаковым именем.</summary>
    public string Subtitle => Country.Name is { } country
        ? $"{country} · {Profile.Host} · {Profile.Protocol.DisplayName()}"
        : $"{Profile.Host} · {Profile.Protocol.DisplayName()}";

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
    /// <summary>
    /// Протокол вместе с транспортом — то, что живёт или умирает целиком. gRPC и
    /// Vision одного протокола режут по-разному, поэтому это разные семейства.
    /// </summary>
    public string Family => Profile.Parameters.Kind switch
    {
        VpnProtocol.Hysteria2 => "Hysteria2",
        _ when Transport.Contains("gRPC", StringComparison.Ordinal) => $"{Profile.Protocol.DisplayName()} · gRPC",
        _ => Profile.Protocol.DisplayName(),
    };

    public string? FeaturedLabel => Profile.FeaturedLabel;

    public bool IsFeatured => Profile.IsFeatured;

    [ObservableProperty]
    private bool _isSelected;

    // MARK: Задержка

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingText))]
    [NotifyPropertyChangedFor(nameof(IsPingGood))]
    [NotifyPropertyChangedFor(nameof(IsPingFair))]
    [NotifyPropertyChangedFor(nameof(IsPingPoor))]
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
            if (Reachable == false) return AddressSilent ? "адрес недоступен" : "не проходит";
            if (Ping is { } ms) return $"{ms} мс";
            return _measured ? "нет ответа" : "";
        }
    }

    // MARK: Доступность

    /// <summary>
    /// Прошёл ли через узел настоящий запрос при последней проверке. Пусто — не
    /// проверяли. Отдельно от задержки: «нет цифры» и «не проходит» — разные вещи,
    /// и выбирать узел автоматически можно только по второму.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingText))]
    [NotifyPropertyChangedFor(nameof(IsPingPoor))]
    [NotifyPropertyChangedFor(nameof(IsUnreachable))]
    private bool? _reachable;

    /// <summary>
    /// Молчит весь адрес: ни один узел на нём не прошёл. Так выглядит блокировка
    /// адреса по дороге — в отличие от случая, когда на том же сервере не проходит
    /// один протокол, а другой работает.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingText))]
    private bool _addressSilent;

    public bool IsUnreachable => Reachable == false;

    /// <summary>
    /// Разряд задержки. Пороги те же, что на других платформах: до восьмидесяти —
    /// хорошо, до ста шестидесяти — терпимо, дальше — плохо.
    ///
    /// <para>Отдаётся разрядом, а не готовой кистью: оформлений два, и кисть,
    /// созданная здесь, знала бы только про одно из них. Цвет назначает стиль,
    /// и подмена набора перекрашивает список сама.</para>
    /// </summary>
    public bool IsPingGood => Ping is { } ms && ms < GoodBelowMs;

    public bool IsPingFair => Ping is { } ms && ms >= GoodBelowMs && ms < FairBelowMs;

    public bool IsPingPoor => Reachable == false || (Ping is { } ms && ms >= FairBelowMs);

    private const int GoodBelowMs = 80;
    private const int FairBelowMs = 160;

    /// <summary>Отметить, что замер был. До первого замера пустое поле честнее прочерка.</summary>
    public void MarkMeasured()
    {
        _measured = true;
        OnPropertyChanged(nameof(HasPing));
        OnPropertyChanged(nameof(PingText));
    }
}
