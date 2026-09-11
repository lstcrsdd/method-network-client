// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using Method.Core.Json;
using Method.Core.Models;

namespace Method.Core.Storage;

/// <summary>Оформление клиента. Различаются только видом, логика одна и та же.</summary>
public enum AppSkin
{
    /// <summary>Основное: тёмное, зелёный акцент, как на других платформах.</summary>
    Base,

    /// <summary>Конструктивистское: красное, плакатная типографика, острые формы.</summary>
    Poster,
}

/// <summary>
/// Настройки клиента.
///
/// <para>Открытым текстом и отдельно от профилей: секретов здесь нет, а
/// шифровать то, что не секрет, значит сделать файл нечитаемым для человека без
/// единой выгоды. Профили лежат рядом и зашифрованы — там пароли.</para>
/// </summary>
public sealed record AppSettings
{
    [JsonPropertyName("skin")]
    public AppSkin Skin { get; init; } = AppSkin.Base;

    [JsonPropertyName("killSwitch")]
    public bool KillSwitch { get; init; }

    [JsonPropertyName("blockLanDns")]
    public bool BlockLanDns { get; init; }

    [JsonPropertyName("autoReconnect")]
    public bool AutoReconnect { get; init; } = true;

    /// <summary>
    /// Доверить выбор узла клиенту: весь трафик, не описанный сценариями, идёт
    /// через тот узел, что отвечает быстрее прочих.
    ///
    /// <para>По умолчанию выключено. Пока человек не сказал иначе, трафик идёт
    /// тем узлом, который он выбрал сам: уводить его куда-то по своему
    /// усмотрению без спроса клиент не должен.</para>
    /// </summary>
    [JsonPropertyName("autoRouting")]
    public bool AutoRouting { get; init; }

    /// <summary>Узел, выбранный в прошлый раз. Пусто — выберется первый.</summary>
    [JsonPropertyName("selectedServer")]
    public Guid? SelectedServer { get; init; }

    /// <summary>
    /// Правила маршрутизации в том порядке, в каком их применяет движок.
    /// Секретов в них нет — адреса и домены человек и сам видит в интерфейсе.
    /// </summary>
    [JsonPropertyName("rules")]
    public IReadOnlyList<RouteRule> Rules { get; init; } = [];
}

/// <summary>Чтение и запись настроек.</summary>
public sealed class AppSettingsStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "settings.json");

    public static AppSettingsStore Default() => new(AppPaths.UserDataDir);

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), MethodJson.Options)
                   ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Испорченный файл настроек не повод не запуститься: берём умолчания.
            // Затирать его не будем — при следующей записи он и так перезапишется,
            // а до тех пор у него есть шанс на разбор.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // Через временный файл: прерванная запись на месте оставляет обрезанный
            // файл, и клиент в следующий раз откроется с чужими настройками.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, MethodJson.Readable));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Записать некуда. Ронять из-за настроек нельзя — они не то, ради чего
            // человек открыл клиент.
        }
    }
}
