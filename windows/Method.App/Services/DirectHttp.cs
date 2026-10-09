// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

namespace Method.App.Services;

/// <summary>
/// HTTP для собственных запросов клиента: подписки, проверки проходимости,
/// внешнего адреса, статистики движка.
///
/// <para><b>Мимо системного прокси — всегда.</b> По умолчанию <see cref="HttpClient"/>
/// берёт прокси из настроек Windows, а туда свой порт прописывают чужие клиенты
/// (см. <see cref="SystemProxy"/>). Тогда проверка «идёт ли трафик» мерила бы не наш
/// туннель, а чужой порт: при живом прокси — чужой туннель, при мёртвом — мгновенный
/// отказ. Ровно так и было: проверка проходимости раз за разом падала на мёртвом
/// <c>127.0.0.1:10809</c> выключенного Happ, клиент переподключал исправный туннель
/// и перебирал узлы один за другим.</para>
///
/// <para>Любой новый <c>new HttpClient()</c> в окне — повод заглянуть сюда.</para>
/// </summary>
public static class DirectHttp
{
    /// <summary>Обработчик без прокси. Остальные настройки — на усмотрение вызывающего.</summary>
    public static SocketsHttpHandler Handler() => new() { UseProxy = false };

    /// <summary>Клиент без прокси с заданным сроком на запрос.</summary>
    public static HttpClient Create(TimeSpan timeout) => new(Handler()) { Timeout = timeout };
}
