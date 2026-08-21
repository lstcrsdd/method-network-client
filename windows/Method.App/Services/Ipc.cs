// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json.Serialization;

namespace Method.App.Services;

/// <summary>
/// Разговор интерфейса с привилегированной частью: именованный канал, по строке
/// JSON на команду и на ответ.
/// </summary>
public static class Ipc
{
    /// <summary>
    /// Имя канала. Своё, ни с чем не пересекающееся: на машине человека рядом
    /// вполне может работать другой клиент того же семейства, и делить с ним
    /// канал управления нельзя ни при каких обстоятельствах.
    /// </summary>
    public const string PipeName = "MethodTunnelHost";

    public sealed class Request
    {
        /// <summary>start | stop | status | version | reconnect</summary>
        [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";

        /// <summary>Конфигурация sing-box для команды start.</summary>
        [JsonPropertyName("config")] public string? Config { get; set; }

        [JsonPropertyName("killSwitch")] public bool KillSwitch { get; set; }

        /// <summary>Запретить запросы DNS в локальную сеть.</summary>
        [JsonPropertyName("blockLanDns")] public bool BlockLanDns { get; set; }
    }

    public sealed class Response
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("running")] public bool Running { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }

        /// <summary>
        /// Операция удалась, но часть защиты не применилась. Отдельно от ошибки
        /// намеренно: «туннель поднят, а kill switch включить не удалось» — это не
        /// провал подключения, и валить из-за этого старт нельзя. Но и молчать
        /// нельзя: иначе человек видит бейдж защиты при нулевой защите.
        /// </summary>
        [JsonPropertyName("warning")] public string? Warning { get; set; }
    }
}
