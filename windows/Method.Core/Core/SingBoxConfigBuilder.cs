// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;
using System.Text.Json;
using Method.Core.Models;

namespace Method.Core.Core;

/// <summary>
/// Собирает конфигурацию sing-box из <see cref="ServerProfile"/> для режима TUN.
///
/// Схема выверена против sing-box 1.13.x (<c>sing-box check</c>):
/// <list type="bullet">
///   <item><c>route.default_domain_resolver</c> обязателен (миграция 1.12);</item>
///   <item>новый формат DNS-серверов (<c>type</c>/<c>server</c>);</item>
///   <item>анализ трафика и перехват DNS — действия маршрута, а не поля inbound.</item>
/// </list>
///
/// Отличия от macOS ровно два, и оба выстраданы на живых машинах: стек TUN и
/// строгая маршрутизация. Оба описаны на месте.
/// </summary>
public static class SingBoxConfigBuilder
{
    public sealed record Options
    {
        /// <summary>Адрес Clash API (управление и статистика трафика).</summary>
        public string ClashController { get; init; } = "127.0.0.1:9090";

        /// <summary>
        /// Секрет Clash API. Умолчания намеренно нет: зашитый в открытый исходник
        /// секрет — это отсутствие секрета. Приложение выдаёт случайный на запуск.
        /// </summary>
        public required string ClashSecret { get; init; }

        public string LogLevel { get; init; } = "info";

        /// <summary>
        /// MTU. 1380, а не 1400: наружу пакет уходит внутри датаграммы QUIC вместе
        /// с заголовками Hysteria2/QUIC/UDP/IP, и при 1400 итог не пролезал в путь
        /// мобильного оператора — соединение флапало, а DNS не укладывался в
        /// таймаут. 1380 оставляет около 70 байт запаса.
        /// </summary>
        public int Mtu { get; init; } = 1380;

        /// <summary>
        /// Имя сетевого адаптера. Задаётся явно, потому что иначе Wintun называет
        /// адаптер по-своему, и найти его потом, чтобы убрать за собой, не по чему.
        /// </summary>
        public string InterfaceName { get; init; } = "Method";

        /// <summary>
        /// Стек TUN. На Windows «gvisor», а не «system», как на macOS.
        /// Значение проверяется по списку: править настройки руками — штатный
        /// способ разобраться, а опечатка вроде «System» с большой буквы проходит
        /// насквозь и роняет движок сырым «unknown stack» уже в рантайме.
        /// </summary>
        public string TunStack { get; init; } = "gvisor";

        /// <summary>
        /// Строгая маршрутизация. На Windows выключена, и это не небрежность:
        /// её агрессивные правила WFP на части машин (антивирус, сторонний
        /// брандмауэр, нестандартный сетевой стек) блокировали весь трафик —
        /// «подключено, но ничего не грузится». Поведение машинно-зависимое, на
        /// машине разработчика конфликта нет, поэтому проверкой оно не ловится.
        /// Автоматические маршруты остаются: трафик идёт через туннель, просто
        /// без жёстких противоутечных фильтров.
        ///
        /// Утечку закрывает не эта настройка, а правило брандмауэра, которое
        /// ставится на время работы туннеля.
        /// </summary>
        public bool StrictRoute { get; init; }

        /// <summary>
        /// Правила человека: что вести мимо туннеля, а что в него.
        ///
        /// <para>Порядок сохраняется — движок применяет первое подошедшее. Они
        /// встают после служебных правил, но до правила о частных адресах:
        /// иначе локальную сеть нельзя было бы завернуть в туннель, даже если
        /// это ровно то, что человеку нужно. Единственное, что пользовательским
        /// правилом не перебить, — адрес самой ноды: пакеты к ней обязаны идти
        /// мимо туннеля, который они же и поднимают.</para>
        /// </summary>
        public IReadOnlyList<RouteRule> Rules { get; init; } = [];
    }

    private static readonly string[] AllowedTunStacks = ["gvisor", "system", "mixed"];

    // MARK: Теги

    private const string ProxyTag = "proxy";
    private const string DirectTag = "direct";

    /// <summary>Резолвер внутри туннеля — всё, что ядро разрешает само.</summary>
    private const string RemoteDnsTag = "remote";

    /// <summary>Резолвер мимо туннеля — только адрес самой ноды.</summary>
    private const string BootstrapDnsTag = "bootstrap";

    // MARK: Сборка

    /// <summary>Конфигурация в виде дерева словарей.</summary>
    public static Dictionary<string, object?> Build(ServerProfile profile, Options options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        if (!AllowedTunStacks.Contains(options.TunStack, StringComparer.Ordinal))
            throw new ArgumentException(
                $"Неизвестный стек TUN «{options.TunStack}». Допустимы: {string.Join(", ", AllowedTunStacks)}.",
                nameof(options));

        // Адрес самой ноды разрешается отдельным резолвером, мимо туннеля. Иначе
        // получается замыкание: чтобы поднять туннель, надо узнать адрес ноды
        // (у Trojan это домен, а не IP), а чтобы узнать адрес — нужен уже
        // поднятый туннель.
        var proxy = Outbound(profile);
        proxy["domain_resolver"] = new Dictionary<string, object?> { ["server"] = BootstrapDnsTag };

        return new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["level"] = options.LogLevel, ["timestamp"] = true },
            ["dns"] = Dns(),
            ["inbounds"] = new object[] { TunInbound(options) },
            ["outbounds"] = new object[]
            {
                proxy,
                new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = DirectTag },
            },
            ["route"] = Route(profile, options),
            ["experimental"] = new Dictionary<string, object?>
            {
                ["clash_api"] = new Dictionary<string, object?>
                {
                    ["external_controller"] = options.ClashController,
                    ["secret"] = options.ClashSecret,
                },
            },
        };
    }

    /// <summary>Готовый JSON для <c>sing-box run -c</c>.</summary>
    public static string BuildJson(ServerProfile profile, Options options) =>
        JsonSerializer.Serialize(Build(profile, options), ConfigJsonOptions);

    /// <summary>
    /// Локальная конфигурация с mixed-инбаундом — чтобы проверить сервер, не
    /// поднимая системный туннель. Ни маршрутов, ни адаптера: если сервер не
    /// отвечает, об этом станет известно до того, как трогали сеть машины.
    /// </summary>
    public static string BuildProbeJson(ServerProfile profile, int listenPort, string? bindInterface = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var proxy = Outbound(profile);
        if (!string.IsNullOrEmpty(bindInterface)) proxy["bind_interface"] = bindInterface;

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["level"] = "error", ["timestamp"] = true },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "mixed",
                    ["tag"] = "probe-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = listenPort,
                },
            },
            ["outbounds"] = new object[] { proxy },
            ["route"] = new Dictionary<string, object?> { ["final"] = ProxyTag },
        };

        return JsonSerializer.Serialize(config, ConfigJsonOptions);
    }

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        WriteIndented = true,
        // Косые черты в путях WebSocket и в именах gRPC-сервисов должны остаться
        // косыми чертами: конфигурацию читают глазами, когда что-то не встало.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // MARK: Секции

    private static Dictionary<string, object?> Dns() => new()
    {
        ["servers"] = new object[]
        {
            // Обычный UDP внутрь туннеля, а не DNS-over-TLS. DoT держит долгоживущий
            // сеанс TCP, и внутри QUIC он залипал: соединение к 1.1.1.1:853 висело
            // десятками секунд и валилось в «context deadline exceeded», из-за чего
            // вставал резолв и весь клиент выглядел нерабочим при живом туннеле.
            // Приватность не страдает — запрос идёт внутри шифрованного канала.
            new Dictionary<string, object?>
            {
                ["tag"] = RemoteDnsTag, ["type"] = "udp", ["server"] = "1.1.1.1", ["detour"] = ProxyTag,
            },
            // Единственный резолвер, ходящий мимо туннеля, и нужен он ровно для
            // одного: узнать адрес самой ноды до того, как туннель встал. Ссылается
            // на него только domain_resolver у proxy-outbound — ни final, ни какое
            // бы то ни было правило сюда не ведут, поэтому имена, которые открывает
            // человек, сюда попасть не могут.
            //
            // detour здесь ставить нельзя: ядро отвечает FATAL «detour to an empty
            // direct outbound makes no sense» — проверено на 1.13.13. Отсутствие
            // detour и означает «напрямую».
            new Dictionary<string, object?>
            {
                ["tag"] = BootstrapDnsTag, ["type"] = "udp", ["server"] = "77.88.8.8",
            },
        },
        ["final"] = RemoteDnsTag,
        // Серверы бывают без IPv6, а TUN ловит и его: prefer_ipv4 давал чёрную
        // дыру на записях AAAA.
        ["strategy"] = "ipv4_only",
    };

    private static Dictionary<string, object?> TunInbound(Options options) => new()
    {
        ["type"] = "tun",
        ["tag"] = "tun-in",
        ["interface_name"] = options.InterfaceName,
        // Только IPv4, и это тоже разбор живого отказа: с IPv6-адресом движок падал
        // на старте с «configure tun interface: set ipv6 address: Element not found»
        // и оставлял после себя полусозданный адаптер Wintun. Следующая попытка
        // подключения об этот обломок подвисала на десятки секунд и заканчивалась
        // таймаутом — со стороны «клиент сломался сам по себе».
        //
        // 198.18.0.0/15 — диапазон RFC 2544, отведённый под тестирование
        // оборудования; реальные сети им не пользуются. Раньше здесь было
        // 172.18.0.1/30, и это ломало клиент в корпоративных сетях: офисный
        // маршрутизатор раздавал маршрут на весь 172.16/12, наш адрес оказывался
        // внутри чужого маршрута, и туннель не вставал. Снаружи всё выглядело
        // как «сервер не отвечает».
        ["address"] = new object[] { "198.18.2.1/30" },
        ["mtu"] = options.Mtu,
        ["auto_route"] = true,
        ["strict_route"] = options.StrictRoute,
        ["stack"] = options.TunStack,
    };

    private static Dictionary<string, object?> Route(ServerProfile profile, Options options) => new()
    {
        // Всё, что ядро разрешает само, уходит внутрь туннеля. Замыкания не
        // возникает: адрес самой ноды разрешает domain_resolver у proxy-outbound,
        // а не этот резолвер.
        //
        // Следствие, о котором надо знать: соединение, отправленное правилом в
        // direct по доменному имени (сегодня такое правило ровно одно — адрес
        // самой ноды), резолвится через туннель. Выбор в пользу отказа-закрытия:
        // при мёртвом туннеле имя не разрешится вовсе, зато мимо туннеля не уйдёт
        // ничего.
        ["default_domain_resolver"] = new Dictionary<string, object?> { ["server"] = RemoteDnsTag },
        ["rules"] = Rules(profile, options),
        ["final"] = ProxyTag,
        ["auto_detect_interface"] = true,
    };

    /// <summary>
    /// Правила маршрутизации по порядку применения: служебные, адрес ноды,
    /// правила человека, частные адреса.
    /// </summary>
    private static object[] Rules(ServerProfile profile, Options options)
    {
        var rules = new List<object>
        {
            new Dictionary<string, object?> { ["action"] = "sniff" },
            new Dictionary<string, object?> { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            ServerDirectRule(profile.Host),
        };

        foreach (var rule in options.Rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Value)) continue;

            rules.Add(UserRule(rule));
        }

        rules.Add(new Dictionary<string, object?> { ["ip_is_private"] = true, ["outbound"] = DirectTag });

        return rules.ToArray();
    }

    /// <summary>
    /// Правило человека. Домен берём вместе с поддоменами: написав «example.com»,
    /// имеют в виду и «www.example.com» — обратное пришлось бы объяснять.
    /// </summary>
    private static Dictionary<string, object?> UserRule(RouteRule rule)
    {
        var outbound = rule.Aim == RouteAim.Tunnel ? ProxyTag : DirectTag;

        var key = rule.Match switch
        {
            RouteMatch.Cidr => "ip_cidr",
            RouteMatch.Process => "process_name",
            RouteMatch.ProcessPath => "process_path",
            _ => "domain_suffix",
        };

        return new Dictionary<string, object?> { [key] = new[] { rule.Value }, ["outbound"] = outbound };
    }

    /// <summary>
    /// Сама нода — мимо туннеля. Без этого правила пакеты к ней уходили бы в
    /// туннель, который они же и поднимают.
    /// </summary>
    private static Dictionary<string, object?> ServerDirectRule(string host)
    {
        if (host.Contains(':'))
            return new Dictionary<string, object?> { ["ip_cidr"] = new[] { host + "/128" }, ["outbound"] = DirectTag };

        if (IsIPv4Address(host))
            return new Dictionary<string, object?> { ["ip_cidr"] = new[] { host + "/32" }, ["outbound"] = DirectTag };

        return new Dictionary<string, object?> { ["domain"] = new[] { host }, ["outbound"] = DirectTag };
    }

    private static bool IsIPv4Address(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4) return false;

        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet)) return false;
            if (octet is < 0 or > 255) return false;
        }

        return true;
    }

    // MARK: Outbound по протоколу

    /// <summary>Outbound одного маршрута. Открыт, потому что он же нужен схеме с полосами.</summary>
    public static Dictionary<string, object?> Outbound(ServerProfile profile) => profile.Parameters.Kind switch
    {
        VpnProtocol.Hysteria2 => Hysteria2Outbound(profile, profile.Parameters.Hysteria2!),
        VpnProtocol.VlessReality => VlessOutbound(profile, profile.Parameters.VlessReality!),
        VpnProtocol.Trojan => TrojanOutbound(profile, profile.Parameters.Trojan!),
        VpnProtocol.Shadowsocks => ShadowsocksOutbound(profile, profile.Parameters.Shadowsocks!),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Parameters.Kind, "Неизвестный протокол"),
    };

    private static Dictionary<string, object?> Hysteria2Outbound(ServerProfile profile, Hysteria2Parameters p)
    {
        var outbound = new Dictionary<string, object?>
        {
            ["type"] = "hysteria2",
            ["tag"] = ProxyTag,
            ["server"] = profile.Host,
            ["server_port"] = profile.Port,
            ["password"] = p.Password,
            ["tls"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = string.IsNullOrEmpty(p.Sni) ? profile.Host : p.Sni,
                ["insecure"] = p.AllowInsecure,
                ["alpn"] = p.Alpn,
            },
        };

        if (p.UpMbps is { } up) outbound["up_mbps"] = up;
        if (p.DownMbps is { } down) outbound["down_mbps"] = down;
        if (!string.IsNullOrEmpty(p.ObfsPassword))
            outbound["obfs"] = new Dictionary<string, object?> { ["type"] = "salamander", ["password"] = p.ObfsPassword };

        // Прыжки по портам намеренно не включаются, хотя поля для них в модели
        // есть. Замер по четыре запроса на каждый из шести инстансов: с прыжками
        // проходило 0–2 из 4, на фиксированном порту — 4 из 4 везде. За NAT смена
        // порта раз в тридцать секунд плодит записи трансляции быстрее, чем
        // маршрутизатор успевает их удерживать, и часть сессий отваливается.
        return outbound;
    }

    private static Dictionary<string, object?> VlessOutbound(ServerProfile profile, VlessRealityParameters p)
    {
        var outbound = new Dictionary<string, object?>
        {
            ["type"] = "vless",
            ["tag"] = ProxyTag,
            ["server"] = profile.Host,
            ["server_port"] = profile.Port,
            ["uuid"] = p.Uuid,
            ["packet_encoding"] = "xudp",
            ["tls"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = p.Sni,
                ["utls"] = new Dictionary<string, object?> { ["enabled"] = true, ["fingerprint"] = p.Fingerprint },
                ["reality"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["public_key"] = p.PublicKey,
                    ["short_id"] = p.ShortId,
                },
            },
        };

        if (!string.IsNullOrEmpty(p.GrpcServiceName))
        {
            // gRPC несовместим с flow: Vision работает только поверх голого TCP, и
            // вместе они дают отказ аутентификации на стороне сервера — соединение
            // принимается, но данные не идут.
            outbound["transport"] = new Dictionary<string, object?>
            {
                ["type"] = "grpc",
                ["service_name"] = p.GrpcServiceName,
            };
        }
        else if (!string.IsNullOrEmpty(p.Flow))
        {
            outbound["flow"] = p.Flow;
        }

        return outbound;
    }

    /// <summary>
    /// Trojan поверх настоящего TLS. В отличие от Reality тут ничего не
    /// подделывается: сервер предъявляет подлинный сертификат, поэтому
    /// server_name обязан совпадать с именем в нём. Клиента без верного пароля
    /// сервер отправляет на запасной сайт — снаружи это неотличимо от HTTPS.
    /// </summary>
    private static Dictionary<string, object?> TrojanOutbound(ServerProfile profile, TrojanParameters p)
    {
        var tls = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["server_name"] = string.IsNullOrEmpty(p.Sni) ? profile.Host : p.Sni,
            ["insecure"] = p.AllowInsecure,
        };

        if (!string.IsNullOrEmpty(p.Fingerprint))
            tls["utls"] = new Dictionary<string, object?> { ["enabled"] = true, ["fingerprint"] = p.Fingerprint };

        var outbound = new Dictionary<string, object?>
        {
            ["type"] = "trojan",
            ["tag"] = ProxyTag,
            ["server"] = profile.Host,
            ["server_port"] = profile.Port,
            ["password"] = p.Password,
            ["tls"] = tls,
        };

        if (!string.IsNullOrEmpty(p.GrpcServiceName))
        {
            outbound["transport"] = new Dictionary<string, object?>
            {
                ["type"] = "grpc",
                ["service_name"] = p.GrpcServiceName,
            };
        }
        else if (!string.IsNullOrEmpty(p.WsPath))
        {
            var ws = new Dictionary<string, object?> { ["type"] = "ws", ["path"] = p.WsPath };
            if (!string.IsNullOrEmpty(p.WsHost))
                ws["headers"] = new Dictionary<string, object?> { ["Host"] = p.WsHost };
            outbound["transport"] = ws;
        }

        return outbound;
    }

    /// <summary>
    /// Shadowsocks. Ни TLS, ни рукопожатия — просто шифрованный поток, и потому
    /// форма трафика непохожа ни на Reality, ни на QUIC: полезен как ещё один
    /// независимый путь, когда остальные распознают.
    ///
    /// Имя шифра передаётся ядру как есть: список поддерживаемых у sing-box свой,
    /// и подменять его догадками нельзя.
    /// </summary>
    private static Dictionary<string, object?> ShadowsocksOutbound(ServerProfile profile, ShadowsocksParameters p) => new()
    {
        ["type"] = "shadowsocks",
        ["tag"] = ProxyTag,
        ["server"] = profile.Host,
        ["server_port"] = profile.Port,
        ["method"] = p.Method,
        ["password"] = p.Password,
    };
}
