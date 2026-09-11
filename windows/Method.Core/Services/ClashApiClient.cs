// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Method.Core.Services;

/// <summary>Суммарно переданные байты с момента запуска движка.</summary>
public sealed record TrafficTotals
{
    [JsonPropertyName("uploadTotal")]
    public long Up { get; init; }

    [JsonPropertyName("downloadTotal")]
    public long Down { get; init; }
}

/// <summary>
/// Статистика движка по его же локальному интерфейсу управления.
///
/// <para>Отдаёт он накопленные суммы, а не скорость. Скорость считает вызывающий
/// как разницу между снимками: так она получается честной по фактическому времени
/// между опросами, а не по номинальному периоду таймера, который на загруженной
/// машине плывёт.</para>
/// </summary>
public sealed class ClashApiClient(HttpClient http, string address, string secret) : IRouteDelaySource, ILaneSwitch
{
    public async Task<TrafficTotals?> ReadTotalsAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{address}/connections");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content
                .ReadFromJsonAsync<TrafficTotals>(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Движок мог не подняться, остановиться или ещё не открыть интерфейс.
            // Для показа скорости это одно и то же: цифры сейчас нет.
            return null;
        }
    }

    /// <summary>
    /// Поставить полосе маршрут: переключить выбор в её группе.
    ///
    /// <para>Через управление, а не пересборкой конфигурации: пересборка — это
    /// остановка движка, снятие адаптера и новый туннель, то есть обрыв всех
    /// соединений ради смены одного маршрута. Здесь же смена мгновенная и
    /// незаметная — ровно поэтому правило датаплейна называет полосу, а не
    /// маршрут.</para>
    ///
    /// <para>Возвращает, случилось ли переключение. Врать в положительную
    /// сторону нельзя: ядру подтверждают применение, и ложное подтверждение
    /// заставит его считать поставленным то, чего нет.</para>
    /// </summary>
    public async Task<bool> SelectAsync(string lane, string route, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Put, $"http://{address}/proxies/{Uri.EscapeDataString(lane)}")
            {
                Content = JsonContent.Create(new SelectBody { Name = route }),
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Задержка маршрута, измеренная самим движком через этот маршрут.
    ///
    /// <para>Мерить снаружи бессмысленно: TCP-рукопожатие до адреса узла идёт
    /// мимо туннеля и говорит о пути до сервера, а не о том, каков этот сервер
    /// как выход. Движок же ходит настоящим маршрутом, со всеми его
    /// рукопожатиями и обфускацией.</para>
    ///
    /// <para>Пусто — маршрут не ответил в срок. Это не ноль и не «быстро»:
    /// перепутать здесь значит выбрать мёртвый маршрут как лучший.</para>
    /// </summary>
    public async Task<int?> DelayAsync(string route, CancellationToken ct = default)
    {
        try
        {
            var url = $"http://{address}/proxies/{Uri.EscapeDataString(route)}/delay" +
                      "?timeout=3000&url=https%3A%2F%2Fwww.gstatic.com%2Fgenerate_204";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content
                .ReadFromJsonAsync<DelayBody>(timeout.Token)
                .ConfigureAwait(false);

            return body?.Delay > 0 ? body.Delay : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record SelectBody
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    private sealed record DelayBody
    {
        [JsonPropertyName("delay")]
        public int Delay { get; init; }
    }
}
