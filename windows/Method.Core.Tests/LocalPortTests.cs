// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net;
using System.Net.Sockets;
using Method.Core.Core;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Порт канала управления был зашит числом, и клиент не поднимался, если тот же
/// порт уже держал другой движок на машине — например, второй наш клиент. Здесь
/// проверяется, что занятый порт больше не тупик.
/// </summary>
public class LocalPortTests
{
    [Fact]
    public void Свободный_порт_берётся_как_есть()
    {
        var port = LocalPort.Pick();

        Assert.InRange(port, 1, 65535);
    }

    [Fact]
    public void Занятый_порт_обходится()
    {
        // Занимаем предпочитаемый порт и требуем, чтобы выбор ушёл на другой.
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();

        try
        {
            var taken = ((IPEndPoint)busy.LocalEndpoint).Port;

            var port = LocalPort.Pick(taken);

            Assert.NotEqual(taken, port);
            Assert.InRange(port, 1, 65535);
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public void Выбранный_порт_действительно_свободен()
    {
        var port = LocalPort.Pick();

        // Раз выбрали — на нём должно получиться встать. Иначе выбор бесполезен:
        // движок упадёт ровно там же, где падал раньше.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }
}
