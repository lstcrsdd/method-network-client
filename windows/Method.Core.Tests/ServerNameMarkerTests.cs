// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Models;
using Xunit;

namespace Method.Core.Tests;

public class ServerNameMarkerTests
{
    [Fact]
    public void Флаг_страны_не_считается_маркером()
    {
        // Самая дорогая ошибка здесь — вырезать флаг: имена узлов в живых
        // подписках почти все начинаются с него, и список превратился бы в кашу.
        var (name, label) = ServerNameMarker.Strip("🇺🇸 NOT_A_RKN-US · gRPC");

        Assert.Equal("🇺🇸 NOT_A_RKN-US · gRPC", name);
        Assert.Null(label);
    }

    [Fact]
    public void Обычное_имя_не_трогается()
    {
        var (name, label) = ServerNameMarker.Strip("Finland 03");
        Assert.Equal("Finland 03", name);
        Assert.Null(label);
    }

    [Theory]
    [InlineData("[VIP] 🇺🇸 US", "🇺🇸 US", "VIP")]
    [InlineData("(Premium) Finland", "Finland", "PREMIUM")]
    [InlineData("【VIP】Tokyo", "Tokyo", "VIP")]
    [InlineData("US · GOLD", "US", "GOLD")]
    [InlineData("Премиум FI", "FI", "ПРЕМИУМ")]
    [InlineData("US-PRO", "US", "PRO")]
    public void Словесный_маркер_вырезается_и_запоминается(string raw, string expectedName, string expectedLabel)
    {
        var (name, label) = ServerNameMarker.Strip(raw);
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedLabel, label);
    }

    [Theory]
    [InlineData("⭐ Finland")]
    [InlineData("⭐️ Finland")] // с селектором начертания U+FE0F — то же самое
    public void Символьный_маркер_вырезается_с_селектором_и_без(string raw)
    {
        var (name, label) = ServerNameMarker.Strip(raw);
        Assert.Equal("Finland", name);
        Assert.Equal("⭐", label);
    }

    [Fact]
    public void Словесный_маркер_важнее_символьного()
    {
        // Человеку «VIP» говорит больше, чем звёздочка.
        var (name, label) = ServerNameMarker.Strip("⭐ [VIP] Tokyo");
        Assert.Equal("Tokyo", name);
        Assert.Equal("VIP", label);
    }

    [Fact]
    public void Имя_из_одного_маркера_остаётся_как_есть()
    {
        // Чистить не во что: пустая строка в списке хуже, чем звёздочка.
        var (name, label) = ServerNameMarker.Strip("⭐");
        Assert.Equal("⭐", name);
        Assert.Equal("⭐", label);
    }

    [Fact]
    public void Маркер_внутри_слова_не_срабатывает()
    {
        // «Protocol» содержит «pro», но маркером не является.
        var (name, label) = ServerNameMarker.Strip("Protocol Node");
        Assert.Equal("Protocol Node", name);
        Assert.Null(label);
    }

    [Fact]
    public void Пустое_имя_переживает_разбор()
    {
        var (name, label) = ServerNameMarker.Strip("   ");
        Assert.Equal("", name);
        Assert.Null(label);
    }
}
