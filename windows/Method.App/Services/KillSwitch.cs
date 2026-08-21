// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using Method.Core;
using System.Runtime.Versioning;

namespace Method.App.Services;

/// <summary>
/// Kill switch на брандмауэре Windows. Ставит его привилегированная часть.
///
/// <para>Пока включён, исходящий трафик по умолчанию заблокирован, и разрешено
/// только необходимое: сам движок (он держит туннель к серверу через физический
/// адаптер), адаптер туннеля (через него идёт трафик приложений), петля, локальная
/// подсеть и получение адреса. Упал движок — прямой путь остаётся закрыт, и мимо
/// туннеля не уходит ни трафик, ни имена.</para>
///
/// <para>Жизненный цикл: включается при успешном подключении, снимается при явном
/// отключении. При запуске привилегированной части снимается всегда — иначе
/// блокировка пережила бы падение или перезагрузку и оставила машину без сети.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class KillSwitch
{
    private const string RulePrefix = "Method KillSwitch";

    private static readonly string[] RuleNames =
        ["Engine", "Tunnel", "Loopback", "LAN", "DHCP-out"];

    /// <summary>Причина последнего отказа — чтобы вызывающий мог назвать её человеку.</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Включить: сначала разрешить необходимое, только потом заблокировать
    /// остальное. Порядок принципиален — обратный оставил бы машину без сети на
    /// время между двумя вызовами.
    /// </summary>
    public static bool Enable(string enginePath, string tunnelInterfaceName)
    {
        try
        {
            RemoveRules();

            // 1. Движок — до блокировки по умолчанию. Без этого правила блокировка
            //    обрубит и сам туннель: машина останется без сети, и поднять туннель,
            //    чтобы это исправить, будет уже нечем.
            var engineCode = Netsh(
                $"advfirewall firewall add rule name=\"{RulePrefix} Engine\" dir=out action=allow " +
                $"program=\"{enginePath}\" enable=yes profile=any", out var engineOutput);

            Netsh($"advfirewall firewall add rule name=\"{RulePrefix} Loopback\" dir=out action=allow " +
                  "remoteip=127.0.0.1/8 enable=yes profile=any", out _);
            Netsh($"advfirewall firewall add rule name=\"{RulePrefix} LAN\" dir=out action=allow " +
                  "remoteip=LocalSubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,224.0.0.0/4,255.255.255.255 " +
                  "enable=yes profile=any", out _);
            Netsh($"advfirewall firewall add rule name=\"{RulePrefix} DHCP-out\" dir=out action=allow " +
                  "protocol=UDP localport=68 remoteport=67 enable=yes profile=any", out _);

            // 2. Весь исходящий через адаптер туннеля — это трафик приложений внутрь.
            //    netsh не умеет ограничивать правило именем адаптера, поэтому здесь
            //    COM-интерфейс брандмауэра.
            //
            //    Отказ на этом шаге проглатывать нельзя. Иначе движок разрешён, а
            //    трафик приложений в туннель — нет: человек видит «подключено» при
            //    полностью мёртвом интернете.
            if (!AllowTunnelInterface(tunnelInterfaceName, out var tunnelError))
            {
                LastError = $"правило для адаптера туннеля не создано ({tunnelError}) — блокировка не применена";
                RemoveRules();
                return false;
            }

            if (engineCode != 0)
            {
                LastError = $"правило для движка не создано (netsh {engineCode}: {engineOutput}) — блокировка не применена";
                RemoveRules();
                return false;
            }

            // 3. Блокируем исходящий по умолчанию. Входящий оставляем как был.
            //
            // Отметку ставим ДО применения политики, а не после. Если между двумя
            // действиями нас убьют, отметка останется — и следующий запуск вернёт
            // политику. Ошибка в эту сторону возвращает человеку интернет; ошибка
            // в обратную оставляет машину без сети и без объяснения.
            SetMarker();

            var blockCode = Netsh(
                "advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound", out var blockOutput);

            if (blockCode != 0)
            {
                LastError = $"политика блокировки не применена (netsh {blockCode}: {blockOutput})";
                ClearMarker();
                RemoveRules();
                return false;
            }

            LastError = null;
            return true;
        }
        catch (Exception e)
        {
            // Kill switch может не включиться, но ронять привилегированную часть он
            // не должен. При этом машину нельзя оставить наполовину заблокированной,
            // поэтому снимаем принудительно: политику мы к этому моменту могли
            // применить, а отметку — не успеть.
            LastError = e.Message;
            try { Disable(force: true); } catch { /* больше сделать нечего */ }
            return false;
        }
    }

    /// <summary>
    /// Выключить: вернуть политику и снять правила. Истина — только если политика
    /// действительно возвращена. Молчаливый отказ здесь дороже самого отказа:
    /// человек нажал «Отключить», увидел «трафик идёт напрямую», а интернета нет.
    ///
    /// <para><b>Политику возвращаем, только если ставили её мы.</b> На Windows это
    /// одна настройка на всю машину, общая со всем, что умеет так же закрываться.
    /// Возвращать её безусловно значит на своём старте молча выключить защиту у
    /// соседнего клиента — и человек об этом не узнает ниоткуда. Проверено на живой
    /// машине: ровно это и произошло.</para>
    ///
    /// <para><paramref name="force"/> — для аварийного сброса, когда защиту снимают
    /// руками и происхождение политики уже неважно.</para>
    /// </summary>
    public static bool Disable(bool force = false)
    {
        try
        {
            if (!force && !MarkerExists())
            {
                // Политику ставили не мы. Свои правила всё равно уберём: они
                // безвредны без блокирующей политики, но и висеть им незачем.
                RemoveRules();
                LastError = null;
                return true;
            }

            // Сначала политика, потом правила: в обратном порядке между двумя
            // вызовами машина осталась бы с блокировкой и без разрешений.
            var code = Netsh(
                "advfirewall set allprofiles firewallpolicy blockinbound,allowoutbound", out var output);

            RemoveRules();
            ClearMarker();

            if (code == 0)
            {
                LastError = null;
                return true;
            }

            LastError = $"netsh вернул {code}: {output}";
            return false;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Отметка «блокирующую политику поставили мы». Файлом, а не полем в памяти:
    /// смысл её именно в том, чтобы пережить падение и перезагрузку — тогда
    /// следующий запуск вернёт машине сеть.
    /// </summary>
    private static string MarkerPath => Path.Combine(AppPaths.ProgramDataDir, "killswitch.on");

    private static bool MarkerExists()
    {
        try { return File.Exists(MarkerPath); }
        catch (IOException) { return false; }
    }

    private static void SetMarker()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ProgramDataDir);
            File.WriteAllText(MarkerPath, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception)
        {
            // Записать не вышло. Хуже всего было бы промолчать и продолжить: тогда
            // блокировку некому будет снять. Поэтому наружу это уйдёт как отказ
            // включения — вызывающий увидит его в LastError.
            LastError = "не удалось запомнить, что блокировку поставили мы";
        }
    }

    private static void ClearMarker()
    {
        try { File.Delete(MarkerPath); }
        catch (IOException) { /* останется — в следующий раз просто снимем ещё раз */ }
    }

    /// <summary>
    /// Заблокировать исходящий IPv6 в интернет на время работы туннеля.
    ///
    /// <para>Почему отдельным правилом, а не в конфигурации движка: у TUN-инбаунда
    /// намеренно нет IPv6-адреса — его назначение падало и оставляло полусозданный
    /// адаптер. Без адреса sing-box не создаёт IPv6-маршрутов, поэтому правило
    /// маршрута до этого трафика просто не доходит: он идёт мимо движка по
    /// физическому адаптеру, и сайт видит настоящий адрес.</para>
    ///
    /// <para>Ставится при подъёме туннеля независимо от kill switch: утечка есть и
    /// при выключенном тумблере. Локальные адреса не трогаем — локальная сеть
    /// продолжает работать.</para>
    /// </summary>
    public static bool BlockIpv6()
    {
        var code = Netsh(
            $"advfirewall firewall add rule name=\"{RulePrefix} IPv6\" dir=out action=block " +
            "remoteip=2000::/3 enable=yes profile=any", out var output);

        if (code == 0) return true;

        LastError = $"IPv6 не заблокирован (netsh {code}: {output})";
        return false;
    }

    public static void UnblockIpv6() =>
        Netsh($"advfirewall firewall delete rule name=\"{RulePrefix} IPv6\"", out _);

    /// <summary>Те же приватные диапазоны, что и в разрешающем правиле локальной сети.</summary>
    private const string PrivateRanges = "LocalSubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16";

    /// <summary>
    /// Запретить запросы DNS в локальную сеть.
    ///
    /// <para>Зачем: разрешающее правило пускает исходящий на приватные диапазоны
    /// целиком, включая пятьдесят третий порт. Приложение, которому резолвер прописан
    /// явно — адресом маршрутизатора, — спросит его напрямую, мимо туннеля, и
    /// провайдер увидит имена. Блокирующее правило в брандмауэре Windows имеет
    /// приоритет над разрешающим, поэтому одного такого правила достаточно.</para>
    ///
    /// <para>Почему не по умолчанию: вместе с утечкой закрываются внутренние имена
    /// локальной сети — принтеры, хранилища, внутренние сервисы. Выбор оставлен
    /// человеку.</para>
    /// </summary>
    public static bool BlockLanDns()
    {
        var udp = Netsh(
            $"advfirewall firewall add rule name=\"{RulePrefix} LAN-DNS-UDP\" dir=out action=block " +
            $"protocol=UDP remoteport=53 remoteip={PrivateRanges} enable=yes profile=any", out var udpOutput);
        var tcp = Netsh(
            $"advfirewall firewall add rule name=\"{RulePrefix} LAN-DNS-TCP\" dir=out action=block " +
            $"protocol=TCP remoteport=53 remoteip={PrivateRanges} enable=yes profile=any", out var tcpOutput);

        if (udp == 0 && tcp == 0) return true;

        // Полумера здесь опаснее бездействия: закрытый UDP при открытом TCP выглядит
        // как работающая защита, а имена продолжают утекать. Откатываем обе.
        LastError = $"запрет DNS в локальной сети не применён (udp {udp}: {udpOutput}; tcp {tcp}: {tcpOutput})";
        UnblockLanDns();
        return false;
    }

    public static void UnblockLanDns()
    {
        Netsh($"advfirewall firewall delete rule name=\"{RulePrefix} LAN-DNS-UDP\"", out _);
        Netsh($"advfirewall firewall delete rule name=\"{RulePrefix} LAN-DNS-TCP\"", out _);
    }

    private static void RemoveRules()
    {
        foreach (var name in RuleNames)
            Netsh($"advfirewall firewall delete rule name=\"{RulePrefix} {name}\"", out _);
    }

    /// <summary>
    /// Разрешить весь исходящий на адаптере туннеля. Без этого правила блокировка
    /// обрубит трафик приложений внутрь туннеля, и клиент будет «подключён» при
    /// отсутствующем интернете.
    /// </summary>
    private static bool AllowTunnelInterface(string interfaceName, out string error)
    {
        error = "";
        try
        {
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (ruleType is null || policyType is null)
            {
                error = "COM-интерфейс брандмауэра недоступен";
                return false;
            }

            dynamic policy = Activator.CreateInstance(policyType)!;
            dynamic rule = Activator.CreateInstance(ruleType)!;

            rule.Name = $"{RulePrefix} Tunnel";
            rule.Description = "Method: разрешить трафик приложений внутрь туннеля";
            rule.Direction = 2;         // исходящее
            rule.Action = 1;            // разрешить
            rule.Enabled = true;
            rule.InterfaceTypes = "All";
            rule.Interfaces = new object[] { interfaceName };
            rule.Profiles = 0x7FFFFFFF; // все профили
            policy.Rules.Add(rule);

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Запуск netsh с честным кодом возврата. Проглатывать код нельзя: отказ по
    /// правам или из-за групповой политики выглядел бы как успех.
    /// Минус единица — запустить не удалось либо не уложился в срок.
    /// </summary>
    private static int Netsh(string arguments, out string output)
    {
        output = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return -1;

            // Читаем до ожидания: переполнение буфера иначе подвесит процесс, а
            // вместе с ним и того, кто его ждёт.
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                output = "превышено время ожидания netsh";
                return -1;
            }

            output = (stdout + stderr).Trim();
            return process.ExitCode;
        }
        catch (Exception e)
        {
            output = e.Message;
            return -1;
        }
    }
}
