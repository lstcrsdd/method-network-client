// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

namespace Method.Core.Services;

/// <summary>
/// Откуда контур берёт задержку маршрута.
///
/// <para>Отделено от <see cref="ClashApiClient"/> ради проверок: контур решает,
/// кого и когда мерить и как подать замер ядру, и эту логику надо проверять на
/// заданных числах — включая мёртвый маршрут и полностью мёртвую сеть. Гонять
/// такие проверки через живые серверы значит проверять серверы, а не контур.</para>
/// </summary>
public interface IRouteDelaySource
{
    /// <summary>
    /// Задержка маршрута в миллисекундах. Пусто — маршрут не ответил в срок;
    /// это не ноль и не «быстро».
    /// </summary>
    Task<int?> DelayAsync(string route, CancellationToken ct = default);
}
