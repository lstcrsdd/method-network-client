// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Method.App.Services;

/// <summary>
/// Настоящий выход машины в сеть — тот, через который провайдер видит трафик.
///
/// <para>Нужен проверке узлов: её пробный движок привязывается к нему, чтобы путь
/// до узла не завернул в себя поднятый туннель — наш или чужой. Туннели
/// узнаются по описанию адаптера: у них тоже есть шлюз и они тоже «подняты».</para>
/// </summary>
public static class PhysicalInterface
{
    /// <summary>
    /// Туннели, а не выходы. Виртуальные адаптеры Hyper-V, VirtualBox и VMware
    /// сюда нарочно не входят: у внешнего коммутатора Hyper-V именно он и есть
    /// выход машины в сеть — сетевая карта отдана коммутатору и своего адреса не
    /// имеет. Исключи его, и на такой машине проверка узлов не находила выхода
    /// вовсе и шла через туннель. Внутренние виртуальные сети шлюза не имеют и
    /// отсекаются им.
    /// </summary>
    private static readonly string[] Virtual =
        ["sing-tun", "wintun", "wireguard", "tap-windows", "tap-", "openvpn", "tunnel", "vpn", "tailscale", "zerotier", "loopback"];

    /// <summary>Имя интерфейса с маршрутом наружу. Пусто — определить не вышло.</summary>
    public static string? Name()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(n => !Virtual.Any(v => n.Description.Contains(v, StringComparison.OrdinalIgnoreCase)
                                           || n.Name.Contains(v, StringComparison.OrdinalIgnoreCase)))
                .Where(n => n.GetIPProperties().GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)))
                // Проводной выход предпочтительнее беспроводного, если подняты оба:
                // система отдаёт маршрут по умолчанию ему же.
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .Select(n => n.Name)
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
