// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Method.Core.Models;

/// <summary>
/// Параметры одного протокола. Заполнено ровно одно поле, и какое именно —
/// говорит <see cref="Kind"/>.
///
/// Тип создаётся только фабриками и не имеет открытого конструктора: собрать
/// профиль, где <c>Kind</c> обещает Hysteria2, а лежит внутри Trojan,
/// невозможно в принципе. Такое расхождение не падает, а подключает не туда —
/// поэтому закрыто типом, а не проверкой.
///
/// В JSON выглядит как <c>{"kind":"hysteria2","hysteria2":{…}}</c> — дословно
/// так же, как на macOS и Android.
/// </summary>
[JsonConverter(typeof(ServerParametersJsonConverter))]
public sealed record ServerParameters
{
    public VpnProtocol Kind { get; }

    public Hysteria2Parameters? Hysteria2 { get; }
    public VlessRealityParameters? VlessReality { get; }
    public TrojanParameters? Trojan { get; }
    public ShadowsocksParameters? Shadowsocks { get; }

    private ServerParameters(
        VpnProtocol kind,
        Hysteria2Parameters? hysteria2 = null,
        VlessRealityParameters? vlessReality = null,
        TrojanParameters? trojan = null,
        ShadowsocksParameters? shadowsocks = null)
    {
        Kind = kind;
        Hysteria2 = hysteria2;
        VlessReality = vlessReality;
        Trojan = trojan;
        Shadowsocks = shadowsocks;
    }

    public static ServerParameters For(Hysteria2Parameters p) =>
        new(VpnProtocol.Hysteria2, hysteria2: p ?? throw new ArgumentNullException(nameof(p)));

    public static ServerParameters For(VlessRealityParameters p) =>
        new(VpnProtocol.VlessReality, vlessReality: p ?? throw new ArgumentNullException(nameof(p)));

    public static ServerParameters For(TrojanParameters p) =>
        new(VpnProtocol.Trojan, trojan: p ?? throw new ArgumentNullException(nameof(p)));

    public static ServerParameters For(ShadowsocksParameters p) =>
        new(VpnProtocol.Shadowsocks, shadowsocks: p ?? throw new ArgumentNullException(nameof(p)));
}

/// <summary>
/// Читает и пишет размеченное объединение. Ключ <c>kind</c> может стоять и после
/// полезной части, поэтому объект сначала разбирается целиком, а потом читается —
/// потоковое чтение здесь зависело бы от порядка полей у того, кто записал файл.
/// </summary>
public sealed class ServerParametersJsonConverter : JsonConverter<ServerParameters>
{
    public override ServerParameters Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Параметры протокола должны быть объектом");

        if (!root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            throw new JsonException("В параметрах протокола нет поля kind");

        VpnProtocol kind;
        try
        {
            kind = VpnProtocolExtensions.FromWireValue(kindElement.GetString()!);
        }
        catch (FormatException e)
        {
            // Наружу из разбора JSON должен выходить один тип исключения, иначе
            // вызывающий ловит JsonException и пропускает мимо себя FormatException
            // с тем же смыслом.
            throw new JsonException(e.Message, e);
        }

        var payloadName = PayloadName(kind);

        if (!root.TryGetProperty(payloadName, out var payload))
            throw new JsonException($"kind={kindElement.GetString()}, но поля {payloadName} нет");

        var raw = payload.GetRawText();
        return kind switch
        {
            VpnProtocol.Hysteria2 => ServerParameters.For(Deserialize<Hysteria2Parameters>(raw, options)),
            VpnProtocol.VlessReality => ServerParameters.For(Deserialize<VlessRealityParameters>(raw, options)),
            VpnProtocol.Trojan => ServerParameters.For(Deserialize<TrojanParameters>(raw, options)),
            VpnProtocol.Shadowsocks => ServerParameters.For(Deserialize<ShadowsocksParameters>(raw, options)),
            _ => throw new JsonException($"Неизвестный протокол: {kind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ServerParameters value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToWireValue());
        writer.WritePropertyName(PayloadName(value.Kind));

        switch (value.Kind)
        {
            case VpnProtocol.Hysteria2:
                JsonSerializer.Serialize(writer, value.Hysteria2!, options);
                break;
            case VpnProtocol.VlessReality:
                JsonSerializer.Serialize(writer, value.VlessReality!, options);
                break;
            case VpnProtocol.Trojan:
                JsonSerializer.Serialize(writer, value.Trojan!, options);
                break;
            case VpnProtocol.Shadowsocks:
                JsonSerializer.Serialize(writer, value.Shadowsocks!, options);
                break;
            default:
                throw new JsonException($"Неизвестный протокол: {value.Kind}");
        }

        writer.WriteEndObject();
    }

    private static string PayloadName(VpnProtocol kind) => kind switch
    {
        VpnProtocol.Hysteria2 => "hysteria2",
        VpnProtocol.VlessReality => "vlessReality",
        VpnProtocol.Trojan => "trojan",
        VpnProtocol.Shadowsocks => "shadowsocks",
        _ => throw new JsonException($"Неизвестный протокол: {kind}"),
    };

    private static T Deserialize<T>(string raw, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<T>(raw, options)
        ?? throw new JsonException($"Пустые параметры {typeof(T).Name}");
}
