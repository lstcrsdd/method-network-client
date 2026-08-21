// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using Method.Core.Models;

namespace Method.Core.Json;

/// <summary>
/// Пишет протокол строкой, а не числом. Число сломало бы чтение профиля на macOS
/// и Android и, что хуже, пережило бы перестановку значений в перечислении, молча
/// поменяв смысл сохранённых файлов.
/// </summary>
public sealed class VpnProtocolJsonConverter : JsonConverter<VpnProtocol>
{
    public override VpnProtocol Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Протокол должен быть строкой");

        try
        {
            return VpnProtocolExtensions.FromWireValue(reader.GetString()!);
        }
        catch (FormatException e)
        {
            throw new JsonException(e.Message, e);
        }
    }

    public override void Write(Utf8JsonWriter writer, VpnProtocol value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireValue());
}
