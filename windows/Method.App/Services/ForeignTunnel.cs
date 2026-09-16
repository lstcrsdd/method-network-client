// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Method.Core.Core;

namespace Method.App.Services;

/// <summary>
/// Чужой VPN, забирающий весь трафик машины.
///
/// <para><b>Зачем.</b> Два туннеля на одной машине друг другу не соседи. Оба ставят
/// маршрут по умолчанию с одинаковым приоритетом, и Windows выбирает между ними
/// сама; оба перехватывают DNS, и в первые секунды разрешение имён зависает — на
/// живой машине на двенадцать секунд. Уже открытые соединения при этом живут, а
/// новые рвутся: мессенджер работает, остальное «отваливается», и снаружи это
/// выглядит поломкой нашего клиента. Ровно так и случилось при включённом TUN в
/// Happ.</para>
///
/// <para><b>Как узнаём.</b> Не по названию программы — их слишком много, — а по
/// признаку самого конфликта: поднятый виртуальный адаптер туннеля со своим
/// шлюзом. Шлюз и есть то, чем туннель забирает трафик; туннель без шлюза
/// (раздельная маршрутизация, корпоративная сеть) нам не мешает и не
/// считается.</para>
/// </summary>
public static class ForeignTunnel
{
    /// <summary>По каким описаниям адаптер узнаётся как туннель.</summary>
    private static readonly string[] TunnelMarks =
        ["sing-tun", "wintun", "wireguard", "tap-windows", "openvpn", "tunnel", "vpn", "tailscale", "zerotier"];

    /// <summary>
    /// Виртуальные адаптеры, которые туннелями не являются. У виртуального
    /// коммутатора Hyper-V бывает шлюз — когда он и есть выход машины в сеть.
    /// </summary>
    private static readonly string[] NotTunnels = ["hyper-v", "virtualbox", "vmware", "loopback"];

    /// <summary>
    /// Имя чужого туннеля, если он поднят и забирает трафик. Пусто — таких нет или
    /// определить не удалось: сомнение трактуется в пользу подключения.
    /// </summary>
    public static string? Find()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => !string.Equals(n.Name, SingBoxConfigBuilder.TunnelInterfaceName, StringComparison.OrdinalIgnoreCase))
                .Where(IsTunnel)
                .Where(HasGateway)
                .Select(n => n.Name)
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool IsTunnel(NetworkInterface n)
    {
        var text = $"{n.Name} {n.Description}";

        if (NotTunnels.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))) return false;

        return n.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
               || TunnelMarks.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasGateway(NetworkInterface n)
    {
        try
        {
            return n.GetIPProperties().GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
