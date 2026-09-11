// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net;
using System.Net.Sockets;

namespace Method.Core.Core;

/// <summary>
/// Выбор свободного порта на петле.
///
/// <para>Нужен для канала управления движком. Порт был зашит числом, и это
/// ломалось ровно там, где ломаться обиднее всего: если на машине уже работает
/// другой sing-box — например, второй наш клиент, — движок падает на старте с
/// «address already in use», а человек видит только то, что подключиться не
/// вышло.</para>
///
/// <para>Гонку между проверкой и запуском движка это не снимает: порт может
/// занять кто-то ещё в те доли секунды, что мы его отпустили. Снять её нельзя
/// вовсе — слушает не наш процесс, — но вероятность мала, а причина отказа
/// теперь доходит до человека дословно.</para>
/// </summary>
public static class LocalPort
{
    /// <summary>Привычный порт Clash API: с него и начинаем искать.</summary>
    public const int Preferred = 9090;

    /// <summary>
    /// Первый свободный порт начиная с предпочитаемого. Если весь отрезок занят,
    /// берём любой свободный, какой даст система: чужой порт лучше отказа.
    ///
    /// <para>Два вызова подряд вернут одно и то же число: найденный порт тут же
    /// отпускается, и следующий поиск снова находит его первым. Нужны два порта —
    /// второй ищите от <c>первый + 1</c>.</para>
    /// </summary>
    public static int Pick(int preferred = Preferred, int span = 40)
    {
        for (var port = preferred; port < preferred + span; port++)
            if (IsFree(port)) return port;

        return Any();
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int Any()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
