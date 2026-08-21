// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Method.Core.Models;

/// <summary>
/// Разбор share-ссылок в <see cref="ServerProfile"/>.
///
/// Поддерживается:
/// <code>
/// hysteria2://&lt;password&gt;@host:port?sni=&amp;obfs=salamander&amp;obfs-password=&amp;insecure=1#name
/// vless://&lt;uuid&gt;@host:port?security=reality&amp;pbk=&amp;sid=&amp;fp=chrome&amp;sni=&amp;flow=xtls-rprx-vision#name
/// trojan://&lt;password&gt;@host:port?sni=&amp;type=tcp|grpc|ws&amp;fp=#name
/// ss://base64(method:password)@host:port#name   (и старая форма целиком в base64)
/// </code>
/// </summary>
public static class ShareLinkParser
{
    /// <summary>Разобрать одну ссылку.</summary>
    public static ServerProfile Parse(string raw)
    {
        var trimmed = StripPortHopping((raw ?? "").Trim());
        if (trimmed.Length == 0) throw ShareLinkException.EmptyInput();

        // Shadowsocks разбирается до Uri: у него есть историческая форма
        // ss://base64(method:password@host:port), где после схемы идёт сплошной
        // base64, и разборщик URI на нём спотыкается.
        if (trimmed.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            return ParseShadowsocks(trimmed);

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw ShareLinkException.MalformedUrl();

        return uri.Scheme.ToLowerInvariant() switch
        {
            "hysteria2" or "hy2" => ParseHysteria2(uri),
            "vless" => ParseVless(uri),
            "trojan" => ParseTrojan(uri),
            var other => throw ShareLinkException.UnsupportedScheme(other),
        };
    }

    /// <summary>
    /// Разобрать несколько ссылок, по одной на строку. Пустые строки и строки,
    /// начинающиеся с «#», пропускаются: подписка кладёт туда свои метаданные
    /// (profile-title, announce и подобное), а не ссылки.
    ///
    /// Ошибки возвращаются, а не глотаются: строка, которую не разобрали, обязана
    /// быть названа. Молча добавить девять ссылок из десяти — значит оставить
    /// человека выяснять, какой сервер пропал, уже на попытке подключиться.
    /// </summary>
    public static (IReadOnlyList<ServerProfile> Profiles, IReadOnlyList<ShareLinkLineError> Errors) ParseMany(string raw)
    {
        var profiles = new List<ServerProfile>();
        var errors = new List<ShareLinkLineError>();

        // Номер строки считается по исходному тексту вместе с пустыми строками:
        // человек ищет ошибку глазами в своём поле ввода, и номер должен совпасть
        // с тем, что он там видит.
        var lines = (raw ?? "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            try
            {
                profiles.Add(Parse(line));
            }
            catch (ShareLinkException e)
            {
                errors.Add(new ShareLinkLineError(i + 1, e));
            }
        }

        return (profiles, errors);
    }

    /// <summary>
    /// Hysteria2 умеет «прыжки по портам»: сервер объявляет диапазон UDP-портов,
    /// чтобы обойти фильтрацию и приоритизацию по номеру порта. В ссылке это
    /// пишется как <c>host:PORT,START-END</c>, и такой authority невалиден — вся
    /// ссылка перестаёт разбираться. Диапазон срезается, базовый порт остаётся:
    /// сервер слушает и на нём тоже.
    /// </summary>
    private static string StripPortHopping(string raw) =>
        PortHoppingRegex.Replace(raw, ":$1");

    private static readonly Regex PortHoppingRegex = new(
        @":(\d{1,5}),\d{1,5}-\d{1,5}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // MARK: Hysteria2

    private static ServerProfile ParseHysteria2(Uri uri)
    {
        var host = Host(uri);
        var port = uri.Port > 0 ? uri.Port : 443;

        // Пароль лежит в user-info и может содержать двоеточие: у Hysteria2
        // аутентификация часто имеет вид «пользователь:пароль» и передаётся целиком.
        var password = UserInfo(uri);
        if (password.Length == 0) throw ShareLinkException.MissingField("password");

        var q = QueryDict(uri);
        var obfsType = Value(q, "obfs")?.ToLowerInvariant();
        var obfsPassword = obfsType == "salamander" ? Value(q, "obfs-password") : null;

        var parameters = new Hysteria2Parameters
        {
            Password = password,
            Sni = Value(q, "sni", "peer"),
            AllowInsecure = BoolFlag(Value(q, "insecure")),
            ObfsPassword = NullIfEmpty(obfsPassword),
            UpMbps = ParseInt(Value(q, "upmbps")),
            DownMbps = ParseInt(Value(q, "downmbps")),
            Alpn = Value(q, "alpn") is { } alpn
                ? alpn.Split(',', StringSplitOptions.RemoveEmptyEntries)
                : ["h3"],
        };

        var (name, label) = Naming(uri, host);
        return new ServerProfile
        {
            Name = name,
            Host = host,
            Port = port,
            Parameters = ServerParameters.For(parameters),
            FeaturedLabel = label,
        };
    }

    // MARK: VLESS + Reality

    private static ServerProfile ParseVless(Uri uri)
    {
        var host = Host(uri);
        if (uri.Port <= 0) throw ShareLinkException.MissingField("port");

        var uuid = UserInfo(uri);
        if (uuid.Length == 0) throw ShareLinkException.MissingField("uuid");

        var q = QueryDict(uri);
        if (!string.Equals(Value(q, "security"), "reality", StringComparison.OrdinalIgnoreCase))
            throw ShareLinkException.MissingField("security=reality");

        var pbk = Value(q, "pbk");
        if (string.IsNullOrEmpty(pbk)) throw ShareLinkException.MissingField("pbk (public key)");

        var sni = Value(q, "sni", "peer");
        if (string.IsNullOrEmpty(sni)) throw ShareLinkException.MissingField("sni");

        // Транспорт читается обязательно. Пока он не читался, gRPC-ссылка молча
        // разбиралась как голый TCP: запись добавлялась, выглядела рабочей и не
        // подключалась никогда.
        var transport = (Value(q, "type") ?? "tcp").ToLowerInvariant();
        var service = NullIfEmpty(Value(q, "servicename"));

        switch (transport)
        {
            case "tcp":
            case "":
                break;
            case "grpc":
                if (service is null) throw ShareLinkException.MissingField("serviceName (для gRPC)");
                break;
            default:
                // Честный отказ вместо записи, которая добавится и не заработает.
                throw ShareLinkException.UnsupportedTransport(transport);
        }

        var parameters = new VlessRealityParameters
        {
            Uuid = uuid,
            // gRPC несовместим с flow: Vision работает только поверх голого TCP,
            // вместе они дают отказ аутентификации на стороне сервера.
            Flow = transport == "grpc" ? null : NullIfEmpty(Value(q, "flow")),
            Sni = sni,
            PublicKey = pbk,
            ShortId = Value(q, "sid") ?? "",
            Fingerprint = Value(q, "fp") ?? "chrome",
            GrpcServiceName = service,
        };

        var (name, label) = Naming(uri, host);
        return new ServerProfile
        {
            Name = name,
            Host = host,
            Port = uri.Port,
            Parameters = ServerParameters.For(parameters),
            FeaturedLabel = label,
        };
    }

    // MARK: Trojan

    /// <summary>
    /// Trojan держится на подлинности TLS: сервер предъявляет настоящий сертификат,
    /// а клиент без правильного пароля уходит на запасной сайт. Поэтому sni здесь
    /// не маскировка, как в Reality, а имя, которое обязано совпасть с сертификатом.
    /// </summary>
    private static ServerProfile ParseTrojan(Uri uri)
    {
        var host = Host(uri);
        var port = uri.Port > 0 ? uri.Port : 443;

        var password = UserInfo(uri);
        if (password.Length == 0) throw ShareLinkException.MissingField("password");

        var q = QueryDict(uri);
        var transport = (Value(q, "type") ?? "tcp").ToLowerInvariant();

        string? service = null;
        string? wsPath = null;
        string? wsHost = null;

        switch (transport)
        {
            case "tcp":
            case "":
                break;
            case "grpc":
                service = NullIfEmpty(Value(q, "servicename"));
                if (service is null) throw ShareLinkException.MissingField("serviceName (для gRPC)");
                break;
            case "ws":
                wsPath = NullIfEmpty(Value(q, "path")) ?? "/";
                wsHost = NullIfEmpty(Value(q, "host"));
                break;
            default:
                throw ShareLinkException.UnsupportedTransport(transport);
        }

        var parameters = new TrojanParameters
        {
            Password = password,
            Sni = NullIfEmpty(Value(q, "sni", "peer")) ?? host,
            AllowInsecure = BoolFlag(Value(q, "allowinsecure", "insecure")),
            Fingerprint = NullIfEmpty(Value(q, "fp")),
            GrpcServiceName = service,
            WsPath = wsPath,
            WsHost = wsHost,
        };

        var (name, label) = Naming(uri, host);
        return new ServerProfile
        {
            Name = name,
            Host = host,
            Port = port,
            Parameters = ServerParameters.For(parameters),
            FeaturedLabel = label,
        };
    }

    // MARK: Shadowsocks

    /// <summary>
    /// Две несовместимые формы, обе встречаются в живых подписках:
    /// <code>
    /// SIP002:  ss://base64url(method:password)@host:port#name
    /// старая:  ss://base64(method:password@host:port)#name
    /// </code>
    /// Пароль не нормализуется: у шифров семейства 2022 это base64-ключ
    /// фиксированной длины, и любая «чистка» его ломает.
    /// </summary>
    private static ServerProfile ParseShadowsocks(string raw)
    {
        var body = raw["ss://".Length..];

        // Имя за решёткой отрезается сразу: в старой форме оно не входит в base64.
        string? name = null;
        var hash = body.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(body[(hash + 1)..]);
            body = body[..hash];
        }

        // Параметры плагина нам не нужны, но мешать разбору они не должны.
        var question = body.IndexOf('?');
        if (question >= 0) body = body[..question];

        if (body.Length == 0) throw ShareLinkException.MalformedUrl();

        string userInfo;
        string hostPort;

        var at = body.LastIndexOf('@');
        if (at >= 0)
        {
            // SIP002: слева base64 или открытый method:password, справа host:port.
            userInfo = body[..at];
            hostPort = body[(at + 1)..];
            if (!userInfo.Contains(':') && DecodeBase64(userInfo) is { } decodedUser)
                userInfo = decodedUser;
        }
        else
        {
            // Старая форма: base64 закрывает всё целиком.
            var decoded = DecodeBase64(body) ?? throw ShareLinkException.MalformedUrl();
            var innerAt = decoded.LastIndexOf('@');
            if (innerAt < 0) throw ShareLinkException.MalformedUrl();
            userInfo = decoded[..innerAt];
            hostPort = decoded[(innerAt + 1)..];
        }

        var colon = userInfo.IndexOf(':');
        if (colon < 0) throw ShareLinkException.MissingField("method:password");

        var method = userInfo[..colon];
        var password = userInfo[(colon + 1)..];
        if (method.Length == 0) throw ShareLinkException.MissingField("method");
        if (password.Length == 0) throw ShareLinkException.MissingField("password");

        // Хост может быть IPv6 в скобках — тогда двоеточий много, и делить надо
        // по последнему.
        var portColon = hostPort.LastIndexOf(':');
        if (portColon < 0) throw ShareLinkException.MissingField("port");

        var host = hostPort[..portColon].Trim('[', ']');
        if (host.Length == 0) throw ShareLinkException.MissingField("host:port");
        if (!int.TryParse(hostPort[(portColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw ShareLinkException.MissingField("host:port");

        // Маркер ищется только в имени: host задаёт не провайдер вручную, а
        // инфраструктура, и чистить там нечего.
        var marked = name is null ? ((string Name, string? Label)?)null : ServerNameMarker.Strip(name);

        return new ServerProfile
        {
            Name = marked?.Name ?? host,
            Host = host,
            Port = port,
            Parameters = ServerParameters.For(new ShadowsocksParameters { Method = method, Password = password }),
            FeaturedLabel = marked?.Label,
        };
    }

    /// <summary>base64 из ссылок бывает и обычный, и url-safe, и без выравнивания.</summary>
    private static string? DecodeBase64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        if (t.Length % 4 != 0) t += new string('=', 4 - t.Length % 4);

        var buffer = new byte[t.Length / 4 * 3];
        if (!Convert.TryFromBase64String(t, buffer, out var written)) return null;

        try
        {
            return StrictUtf8.GetString(buffer, 0, written);
        }
        catch (DecoderFallbackException)
        {
            // Раскодировалось не в текст — значит это был не base64 от ссылки.
            return null;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // MARK: Общее

    private static string Host(Uri uri)
    {
        // IPv6 разборщик URI отдаёт в скобках; в конфигурацию ядра адрес идёт голым.
        var host = uri.Host.Trim('[', ']');
        if (host.Length == 0) throw ShareLinkException.MissingField("host");
        return host;
    }

    /// <summary>user-info (<c>user[:password]@</c>), раскодированная из процентов.</summary>
    private static string UserInfo(Uri uri) => Uri.UnescapeDataString(uri.UserInfo);

    private static Dictionary<string, string> QueryDict(Uri uri)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = uri.Query;
        if (query.Length <= 1) return d;

        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            // Параметр без значения пропускается: «insecure» без «=1» ничего не
            // утверждает, и додумывать за ссылку значение нельзя.
            if (eq < 0) continue;
            d[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return d;
    }

    /// <summary>Первое из имён, которое есть в запросе. Пустое значение — тоже значение.</summary>
    private static string? Value(Dictionary<string, string> q, params string[] names)
    {
        foreach (var n in names)
            if (q.TryGetValue(n, out var v))
                return v;
        return null;
    }

    /// <summary>Имя записи и метка выделения из фрагмента после «#».</summary>
    private static (string Name, string? Label) Naming(Uri uri, string host)
    {
        var fragment = uri.Fragment;
        if (fragment.Length <= 1) return (host, null);
        return ServerNameMarker.Strip(Uri.UnescapeDataString(fragment[1..]));
    }

    private static bool BoolFlag(string? v) =>
        v?.ToLowerInvariant() is "1" or "true" or "yes";

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static int? ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
