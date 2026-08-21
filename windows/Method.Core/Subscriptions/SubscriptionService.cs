// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Method.Core.Models;

namespace Method.Core.Subscriptions;

/// <summary>
/// Загрузка и разбор подписки.
///
/// <para>Панели отдают разный формат в зависимости от того, каким клиентом им
/// представиться: конфигурацию sing-box, конфигурацию Xray, YAML от Clash или
/// список share-ссылок в base64. Поэтому представляемся несколькими известными
/// именами по очереди — сначала теми, кому отдают формат нашего же движка
/// (меньше всего потерь при разборе), потом откатываемся на обычные ссылки.</para>
/// </summary>
public sealed class SubscriptionService(HttpClient http, IDeviceIdentity device)
{
    /// <summary>
    /// Имена клиентов по порядку надёжности формата, который на них отдают.
    /// </summary>
    private static readonly string[] UserAgents =
    [
        "Happ/2.4.5",
        "Happ",
        "SFA/1.11.0",
        "sing-box/1.13.0",
        "ClashMetaForAndroid/2.11.0",
        "v2rayNG/1.8.32",
        "Method/1.0",
    ];

    public async Task<SubscriptionFetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        Exception last = SubscriptionException.Empty();

        foreach (var userAgent in UserAgents)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await FetchOnceAsync(url, userAgent, ct).ConfigureAwait(false);
            }
            catch (SubscriptionException e)
            {
                // Сообщение провайдера от имени клиента не зависит — пробовать
                // другое имя незачем.
                if (e.Failure == SubscriptionFailure.ProviderMessage) throw;
                last = e;
            }
        }

        throw last;
    }

    private async Task<SubscriptionFetchResult> FetchOnceAsync(string url, string userAgent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveUrl(url));
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        // Панели читают идентификатор из разных заголовков, и единого стандарта
        // нет. Шлём все известные: лишний заголовок панель проигнорирует, а
        // недостающий стоит человеку отказа в выдаче конфигурации.
        foreach (var name in new[] { "X-HWID", "HWID", "Hwid", "X-Method-HWID", "X-Device-HWID", "X-Client-HWID" })
            request.Headers.TryAddWithoutValidation(name, device.Hwid);

        request.Headers.TryAddWithoutValidation("X-Device-ID", device.ShortHwid);
        request.Headers.TryAddWithoutValidation("X-Device-Name", device.DeviceName);
        request.Headers.TryAddWithoutValidation("X-Device-Platform", device.Platform);
        request.Headers.TryAddWithoutValidation("X-Method-HWID-Hash", device.HashedHwid);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        HttpResponseMessage response;
        byte[] body;
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw SubscriptionException.Network("сервер не ответил за 15 с");
        }
        catch (HttpRequestException e)
        {
            throw SubscriptionException.Network(e.Message);
        }

        using (response)
        {
            var htmlError = HtmlErrorMessage(body);
            if (htmlError is not null && (int)response.StatusCode >= 500)
                throw SubscriptionException.Network($"HTTP {(int)response.StatusCode}: {htmlError}");

            if (!response.IsSuccessStatusCode)
                throw SubscriptionException.Network($"HTTP {(int)response.StatusCode}");

            var (profiles, skipped, providerMessage) = ParseBody(body);

            if (profiles.Count == 0)
            {
                if (providerMessage is not null) throw SubscriptionException.ProviderMessage(providerMessage);
                throw SubscriptionException.Empty();
            }

            return BuildResult(response, profiles, skipped);
        }
    }

    // MARK: Разбор тела

    private static (List<ServerProfile> Profiles, int Skipped, string? ProviderMessage) ParseBody(byte[] body)
    {
        var profiles = new List<ServerProfile>();
        var skipped = 0;
        string? providerMessage = null;

        // Первым делом — формат нашего же движка: меньше всего потерь при разборе.
        if (TryReadSingBoxOutbounds(body, out var outbounds))
        {
            foreach (var outbound in outbounds)
            {
                var profile = ProfileFromOutbound(outbound);
                if (profile is not null)
                {
                    profiles.Add(profile);
                    continue;
                }

                var type = TextField(outbound, "type");
                if (type is "vless" or "hysteria2")
                {
                    // Тег есть, а обязательных полей нет — либо заглушка провайдера,
                    // либо формат, который мы не разбираем.
                    providerMessage ??= TextField(outbound, "tag");
                    skipped++;
                }
            }

            return (profiles, skipped, providerMessage);
        }

        var text = DecodeBody(body);

        var htmlError = HtmlErrorMessage(body);
        if (htmlError is not null) throw SubscriptionException.Network(htmlError);

        var (linkProfiles, errors) = ParseShareLinks(text);

        if (linkProfiles.Count == 0 && LooksLikeYaml(text))
        {
            // Тело оказалось YAML. Ошибки разбора ссылок здесь — артефакт: каждая
            // строка YAML не ссылка, и складывать их в счётчик пропущенных значит
            // врать числом. Для чистой подписки на два десятка строк счётчик
            // показывал бы два десятка «непринятых узлов» при нуле потерь, а
            // человек по этому числу решает, всё ли доехало.
            var clash = ParseClashYaml(text);
            profiles.AddRange(clash.Profiles);
            skipped += clash.Skipped;
        }
        else
        {
            profiles.AddRange(linkProfiles);
            skipped += errors.Count;
        }

        if (profiles.Count == 0 && LooksLikeXrayJson(body))
            throw SubscriptionException.UnsupportedFormat("Xray JSON");

        if (profiles.Count == 0) providerMessage = FirstFragmentName(text);

        return (profiles, skipped, providerMessage);
    }

    private static SubscriptionFetchResult BuildResult(
        HttpResponseMessage response, List<ServerProfile> profiles, int skipped)
    {
        var title = DecodeHeaderText(Header(response, "profile-title"));
        var fileName = ExtractFilename(Header(response, "Content-Disposition"));
        var (used, total, expires) = ParseUserinfo(Header(response, "Subscription-Userinfo"));

        return new SubscriptionFetchResult
        {
            Profiles = profiles,
            SuggestedName = title ?? fileName,
            SkippedCount = skipped,
            Title = title,
            Announce = DecodeHeaderText(Header(response, "announce")),
            UpdateIntervalHours = ParseInt(Header(response, "profile-update-interval")),
            WebPageUrl = SafeHttpUrl(Header(response, "profile-web-page-url")),
            SupportUrl = SafeHttpUrl(Header(response, "support-url") ?? Header(response, "announce-url")),
            TrafficUsed = used,
            TrafficTotal = total,
            ExpiresAt = expires,
        };
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        if (response.Content.Headers.TryGetValues(name, out var contentValues)) return contentValues.FirstOrDefault();
        return null;
    }

    // MARK: Конфигурация sing-box

    /// <summary>
    /// Тело подписки приходит с чужого сервера, и разбор JSON здесь рекурсивный.
    /// Глубина ограничена самим разборщиком (по умолчанию 64 уровня), поэтому
    /// вложенный документ даёт ошибку разбора, а не переполнение стека.
    /// </summary>
    private static bool TryReadSingBoxOutbounds(byte[] body, out List<JsonElement> outbounds)
    {
        outbounds = [];

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("outbounds", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                outbounds = list.EnumerateArray().Select(e => e.Clone()).ToList();
                return outbounds.Count > 0;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("outbounds", out var nested)) continue;
                    if (nested.ValueKind != JsonValueKind.Array) continue;

                    outbounds.AddRange(nested.EnumerateArray().Select(e => e.Clone()));
                }

                return outbounds.Count > 0;
            }
        }
        catch (JsonException)
        {
            // Не JSON или слишком вложенный — пойдём другими путями разбора.
        }

        return false;
    }

    private static ServerProfile? ProfileFromOutbound(JsonElement outbound)
    {
        var type = TextField(outbound, "type");
        var server = TextField(outbound, "server");
        var port = IntField(outbound, "server_port");
        if (string.IsNullOrEmpty(server) || port is null) return null;

        var name = TextField(outbound, "tag") ?? server;
        var tls = Child(outbound, "tls");

        if (type == "vless")
        {
            var uuid = TextField(outbound, "uuid");
            var reality = tls is null ? null : Child(tls.Value, "reality");
            var publicKey = reality is null ? null : TextField(reality.Value, "public_key");
            if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(publicKey)) return null;

            var utls = tls is null ? null : Child(tls.Value, "utls");
            var transport = Child(outbound, "transport");
            var service = transport is null ? null : TextField(transport.Value, "service_name");

            return new ServerProfile
            {
                Name = name,
                Host = server,
                Port = port.Value,
                Parameters = ServerParameters.For(new VlessRealityParameters
                {
                    Uuid = uuid,
                    Flow = string.IsNullOrEmpty(service) ? TextField(outbound, "flow") : null,
                    Sni = (tls is null ? null : TextField(tls.Value, "server_name")) ?? server,
                    PublicKey = publicKey,
                    ShortId = (reality is null ? null : TextField(reality.Value, "short_id")) ?? "",
                    Fingerprint = (utls is null ? null : TextField(utls.Value, "fingerprint")) ?? "chrome",
                    GrpcServiceName = string.IsNullOrEmpty(service) ? null : service,
                }),
            };
        }

        if (type == "hysteria2")
        {
            var password = TextField(outbound, "password");
            if (string.IsNullOrEmpty(password)) return null;

            var obfs = Child(outbound, "obfs");

            return new ServerProfile
            {
                Name = name,
                Host = server,
                Port = port.Value,
                Parameters = ServerParameters.For(new Hysteria2Parameters
                {
                    Password = password,
                    Sni = tls is null ? null : TextField(tls.Value, "server_name"),
                    AllowInsecure = tls is not null && BoolField(tls.Value, "insecure"),
                    ObfsPassword = obfs is null ? null : TextField(obfs.Value, "password"),
                    UpMbps = IntField(outbound, "up_mbps"),
                    DownMbps = IntField(outbound, "down_mbps"),
                    Alpn = (tls is null ? null : StringArray(tls.Value, "alpn")) ?? ["h3"],
                }),
            };
        }

        return null;
    }

    private static JsonElement? Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? TextField(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? IntField(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool BoolField(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string>? StringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return null;

        var list = value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();

        return list.Count > 0 ? list : null;
    }

    private static bool LooksLikeXrayJson(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;

            var first = document.RootElement.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.Object
                   && (first.TryGetProperty("routing", out _) || first.TryGetProperty("inbounds", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // MARK: Список ссылок

    private static readonly Regex LinkRegex = new(
        @"(?:hysteria2|hy2|vless|trojan|ss)://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Ссылки выбираются из текста поиском, а не построчно: панели кладут их и в
    /// одну строку, и вперемешку с пояснениями. Схемы перечислены все — иначе
    /// Trojan и Shadowsocks отсеивались бы до разбора и терялись молча, даже не
    /// попав в счётчик пропущенных.
    /// </summary>
    private static (IReadOnlyList<ServerProfile> Profiles, IReadOnlyList<ShareLinkLineError> Errors)
        ParseShareLinks(string text)
    {
        var links = LinkRegex.Matches(text).Select(m => m.Value).ToList();
        return links.Count == 0
            ? ShareLinkParser.ParseMany(text)
            : ShareLinkParser.ParseMany(string.Join("\n", links));
    }

    /// <summary>
    /// Имя первой ссылки. Провайдеры кладут туда сообщение для человека, когда
    /// сервер фиктивный.
    /// </summary>
    private static string? FirstFragmentName(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var hash = line.IndexOf('#');
            if (hash < 0 || hash == line.Length - 1) continue;

            var name = Uri.UnescapeDataString(line[(hash + 1)..]).Trim();
            if (name.Length > 0) return name;
        }

        return null;
    }

    // MARK: YAML от Clash

    private static bool LooksLikeYaml(string text) =>
        text.StartsWith("proxies:", StringComparison.Ordinal)
        || text.Contains("\nproxies:", StringComparison.Ordinal)
        || text.Contains("mixed-port:", StringComparison.Ordinal);

    private static (List<ServerProfile> Profiles, int Skipped) ParseClashYaml(string text)
    {
        var profiles = new List<ServerProfile>();
        var skipped = 0;

        foreach (var proxy in ClashProxyBlocks(text))
        {
            if (!proxy.TryGetValue("type", out var type)) continue;

            var profile = type.ToLowerInvariant() switch
            {
                "vless" => ClashVless(proxy),
                "hysteria2" or "hy2" => ClashHysteria2(proxy),
                _ => null,
            };

            if (profile is not null) profiles.Add(profile);
            else skipped++;
        }

        return (profiles, skipped);
    }

    /// <summary>
    /// Разбор ровно того куска YAML, который нам нужен: список прокси. Полноценный
    /// разборщик YAML сюда не тянем — это зависимость ради одного списка пар
    /// «ключ: значение» с предсказуемой формой.
    /// </summary>
    private static List<Dictionary<string, string>> ClashProxyBlocks(string text)
    {
        var result = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        var inProxies = false;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            if (!inProxies)
            {
                if (trimmed == "proxies:") inProxies = true;
                continue;
            }

            // Началась соседняя секция верхнего уровня — список прокси кончился.
            if (!line.StartsWith(' ') && !line.StartsWith('-')) break;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (current is not null) result.Add(current);
                current = [];
                ParseClashLine(trimmed[2..], current);
            }
            else if (current is not null)
            {
                ParseClashLine(trimmed, current);
            }
        }

        if (current is not null) result.Add(current);
        return result;
    }

    private static void ParseClashLine(string line, Dictionary<string, string> into)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return;

        var key = line[..colon].Trim();
        var value = line[(colon + 1)..].Trim();

        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];

        if (key.Length > 0) into[key] = value;
    }

    private static ServerProfile? ClashVless(Dictionary<string, string> proxy)
    {
        var server = Value(proxy, "server");
        var port = ParseInt(Value(proxy, "port"));
        var uuid = Value(proxy, "uuid");
        var publicKey = Value(proxy, "public-key", "public_key", "reality-opts.public-key");

        if (string.IsNullOrEmpty(server) || port is null
            || string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(publicKey)) return null;

        return new ServerProfile
        {
            Name = Value(proxy, "name") ?? server,
            Host = server,
            Port = port.Value,
            Parameters = ServerParameters.For(new VlessRealityParameters
            {
                Uuid = uuid,
                Flow = NullIfEmpty(Value(proxy, "flow")),
                Sni = Value(proxy, "servername", "sni") ?? server,
                PublicKey = publicKey,
                ShortId = Value(proxy, "short-id", "short_id") ?? "",
                Fingerprint = Value(proxy, "client-fingerprint", "fingerprint") ?? "chrome",
            }),
        };
    }

    private static ServerProfile? ClashHysteria2(Dictionary<string, string> proxy)
    {
        var server = Value(proxy, "server");
        var port = ParseInt(Value(proxy, "port"));
        var password = Value(proxy, "password");

        if (string.IsNullOrEmpty(server) || port is null || string.IsNullOrEmpty(password)) return null;

        var alpn = Value(proxy, "alpn");

        return new ServerProfile
        {
            Name = Value(proxy, "name") ?? server,
            Host = server,
            Port = port.Value,
            Parameters = ServerParameters.For(new Hysteria2Parameters
            {
                Password = password,
                Sni = Value(proxy, "sni", "servername"),
                AllowInsecure = BoolValue(Value(proxy, "skip-cert-verify", "insecure")),
                ObfsPassword = NullIfEmpty(Value(proxy, "obfs-password")),
                UpMbps = ParseInt(Value(proxy, "up")),
                DownMbps = ParseInt(Value(proxy, "down")),
                Alpn = alpn is null
                    ? ["h3"]
                    : alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            }),
        };
    }

    private static string? Value(Dictionary<string, string> proxy, params string[] names)
    {
        foreach (var name in names)
            if (proxy.TryGetValue(name, out var value))
                return value;
        return null;
    }

    private static bool BoolValue(string? value) =>
        value?.ToLowerInvariant() is "true" or "1" or "yes";

    // MARK: Заголовки и тело

    /// <summary>
    /// Тело: если ссылки уже видны — берём как есть, иначе пробуем base64 (в том
    /// числе безопасный для адресов алфавит и без выравнивания).
    /// </summary>
    private static string DecodeBody(byte[] body)
    {
        var raw = Encoding.UTF8.GetString(body).Trim();
        if (raw.Length == 0) return "";
        if (raw.Contains("://", StringComparison.Ordinal)) return raw;
        return DecodeBase64(raw) ?? raw;
    }

    private static string? DecodeBase64(string value)
    {
        var padded = value.Replace("\n", "").Replace("\r", "").Replace('-', '+').Replace('_', '/');
        if (padded.Length % 4 != 0) padded += new string('=', 4 - padded.Length % 4);

        var buffer = new byte[padded.Length / 4 * 3];
        if (!Convert.TryFromBase64String(padded, buffer, out var written)) return null;

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Заголовок приходит либо с приставкой «base64:», либо открытым текстом.
    /// Разбираем оба: часть панелей кодирует всегда, часть — только когда в строке
    /// не-ASCII, и угадывать по содержимому ненадёжно.
    /// </summary>
    private static string? DecodeHeaderText(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        const string prefix = "base64:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value;

        var decoded = DecodeBase64(value[prefix.Length..]);
        return string.IsNullOrEmpty(decoded) ? null : decoded;
    }

    /// <summary>
    /// Адрес из заголовка принимаем только по http и https. Заголовок приходит с
    /// чужого сервера, а открытие произвольной схемы — это право запускать что-то
    /// на машине человека по указанию недоверенного ввода.
    /// </summary>
    private static string? SafeHttpUrl(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;

        return uri.ToString();
    }

    /// <summary>
    /// Некоторые панели выдают подписку по адресу с подстановкой устройства.
    /// </summary>
    private string ResolveUrl(string url)
    {
        string[] placeholders = ["{hwid}", "{HWID}", "{device_id}", "{DEVICE_ID}", "{deviceId}", "{DEVICEID}"];

        foreach (var placeholder in placeholders)
            url = url.Replace(placeholder, device.Hwid, StringComparison.Ordinal);

        return url;
    }

    private static string? ExtractFilename(string? disposition)
    {
        if (string.IsNullOrEmpty(disposition)) return null;

        var index = disposition.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var name = disposition[(index + "filename=".Length)..].Trim(' ', '"');
        return name.Length == 0 ? null : Uri.UnescapeDataString(name);
    }

    /// <summary>Разбор строки вида «upload=123; download=456; total=789; expire=1712345678».</summary>
    private static (long? Used, long? Total, DateTimeOffset? Expires) ParseUserinfo(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return (null, null, null);

        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            if (!long.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                continue;

            values[pair[0].Trim()] = number;
        }

        var hasTraffic = values.ContainsKey("upload") || values.ContainsKey("download");
        long? used = hasTraffic
            ? values.GetValueOrDefault("upload") + values.GetValueOrDefault("download")
            : null;

        long? total = values.TryGetValue("total", out var t) ? t : null;
        DateTimeOffset? expires = values.TryGetValue("expire", out var e)
            ? DateTimeOffset.FromUnixTimeSeconds(e)
            : null;

        return (used, total, expires);
    }

    /// <summary>
    /// Сервер вместо конфигурации отдал страницу. Достаём из неё заголовок —
    /// в нём обычно и написано, что случилось.
    /// </summary>
    private static string? HtmlErrorMessage(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body).Trim();
        if (!text.Contains("<html", StringComparison.OrdinalIgnoreCase)) return null;

        var title = FirstMatch(text, @"<title>\s*([^<]+)\s*</title>");
        if (title is not null) return title;

        var heading = FirstMatch(text, @"<h1>\s*([^<]+)\s*</h1>");
        if (heading is not null) return heading;

        return "сервер подписки вернул страницу вместо конфигурации";
    }

    private static string? FirstMatch(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value.Replace('\n', ' ').Trim() : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}
