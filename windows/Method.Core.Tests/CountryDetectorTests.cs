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

public class CountryDetectorTests
{
    [Fact]
    public void Флаг_опознаётся_и_вырезается_из_имени()
    {
        // Ровно та форма, которую присылает живая подписка.
        var match = CountryDetector.Detect("🇱🇹 NOT_A_RKN-LT · Hysteria2");

        Assert.Equal("LT", match.Code);
        Assert.Equal("Литва", match.Name);
        Assert.Equal("NOT_A_RKN-LT · Hysteria2", match.CleanName);
    }

    [Fact]
    public void Имя_без_флага_не_меняется()
    {
        var match = CountryDetector.Detect("Finland 03");

        Assert.Equal("FI", match.Code);
        Assert.Equal("Finland 03", match.CleanName);
    }

    [Theory]
    [InlineData("🇺🇸 US East", "US")]
    [InlineData("🇫🇮 Helsinki", "FI")]
    [InlineData("🇳🇱 Amsterdam", "NL")]
    public void Флаг_читается_для_разных_стран(string name, string expected) =>
        Assert.Equal(expected, CountryDetector.Detect(name).Code);

    [Theory]
    [InlineData("Германия · Frankfurt", "DE")]
    [InlineData("Нидерланды 01", "NL")]
    [InlineData("Sweden Stockholm", "SE")]
    public void Название_страны_словом_опознаётся(string name, string expected) =>
        Assert.Equal(expected, CountryDetector.Detect(name).Code);

    [Theory]
    [InlineData("NOT_A_RKN-LT · Hysteria2", "LT")]
    [InlineData("node-DE-01", "DE")]
    [InlineData("US East", "US")]
    public void Код_страны_отдельным_словом_опознаётся(string name, string expected) =>
        Assert.Equal(expected, CountryDetector.Detect(name).Code);

    [Fact]
    public void Код_внутри_слова_не_считается_страной()
    {
        // «RUSH» содержит RU, ничего про Россию не означая.
        Assert.Null(CountryDetector.Detect("RUSHnode fast").Code);
    }

    [Fact]
    public void Неопознанное_имя_остаётся_как_есть()
    {
        var match = CountryDetector.Detect("Быстрый узел 7");

        Assert.Null(match.Code);
        Assert.Null(match.Name);
        Assert.Equal("Быстрый узел 7", match.CleanName);
    }

    [Fact]
    public void Пустое_имя_переживает_разбор()
    {
        var match = CountryDetector.Detect("   ");

        Assert.Null(match.Code);
        Assert.Equal("", match.CleanName);
    }

    [Fact]
    public void После_вырезания_флага_не_остаётся_висящих_разделителей()
    {
        // Иначе строка начиналась бы с «· » или с тире.
        Assert.Equal("Amsterdam", CountryDetector.Detect("🇳🇱 · Amsterdam").CleanName);
        Assert.Equal("Berlin", CountryDetector.Detect("🇩🇪 — Berlin").CleanName);
    }
}
