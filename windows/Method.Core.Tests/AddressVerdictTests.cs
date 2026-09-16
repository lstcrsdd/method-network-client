// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Services;
using Xunit;

namespace Method.Core.Tests;

public class AddressVerdictTests
{
    [Fact]
    public void Адрес_молчит_когда_не_прошёл_ни_один_узел()
    {
        // Так выглядела блокировка вживую: Финляндия молчала всеми способами,
        // Литва отвечала всеми.
        var silent = AddressVerdict.Silent(
        [
            ("138.124.99.167", false), ("138.124.99.167", false), ("138.124.99.167", false),
            ("195.66.25.148", true), ("195.66.25.148", true),
        ]);

        Assert.Equal(["138.124.99.167"], silent);
    }

    [Fact]
    public void Адрес_с_одним_живым_протоколом_не_молчит()
    {
        // Германия: TCP не проходил, Hysteria2 по UDP — проходила. Это не
        // блокировка адреса, узел в выборе остаётся.
        var silent = AddressVerdict.Silent([("185.237.14.214", false), ("185.237.14.214", true)]);

        Assert.Empty(silent);
    }

    [Fact]
    public void Непроверенный_узел_не_довод_против_адреса()
    {
        var silent = AddressVerdict.Silent([("62.90.188.252", false), ("62.90.188.252", null)]);

        Assert.Empty(silent);
    }
}
