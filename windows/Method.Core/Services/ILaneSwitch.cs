// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

namespace Method.Core.Services;

/// <summary>
/// Чем полосе ставят маршрут.
///
/// <para>Отделено от <see cref="ClashApiClient"/> ради проверок. Проверять тут
/// надо не HTTP, а порядок: сделали — подтвердили, не сделали — не
/// подтвердили. Ошибка в этом порядке не падает и не видна в журнале: ядро
/// просто считает поставленным маршрут, которого на полосе нет, — и живой
/// сервер для такой проверки бесполезен, потому что отказ движка на нём ещё
/// надо суметь устроить.</para>
/// </summary>
public interface ILaneSwitch
{
    /// <summary>
    /// Поставить полосе маршрут. Возвращает, случилось ли это на самом деле;
    /// врать в положительную сторону нельзя.
    /// </summary>
    Task<bool> SelectAsync(string lane, string route, CancellationToken ct = default);
}
