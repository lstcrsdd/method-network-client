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
public sealed class ClashApiClient(HttpClient http, string address, string secret)
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
}
