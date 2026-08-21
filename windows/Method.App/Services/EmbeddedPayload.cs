// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Reflection;

namespace Method.App.Services;

/// <summary>
/// Движок, вшитый в исполняемый файл. Клиент раздаётся одним файлом, поэтому
/// sing-box и драйвер едут внутри него и извлекаются при первом запуске
/// привилегированной части.
/// </summary>
public static class EmbeddedPayload
{
    public static bool Exists(string logicalName) => Open(logicalName) is not null;

    public static Stream? Open(string logicalName) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);

    /// <summary>
    /// Извлечь вшитый файл, а если его в сборке нет — взять готовый рядом с
    /// исполняемым файлом. Второе нужно для сборок, собранных без движка (отладка
    /// из исходников) и для установки, которая раскладывает файлы отдельно.
    /// </summary>
    public static bool ExtractWithFallback(string logicalName, string targetPath, params string?[] fallbackSources)
    {
        try
        {
            if (Exists(logicalName))
            {
                Extract(logicalName, targetPath);
                return true;
            }
        }
        catch
        {
            // Не вышло из ресурса — пробуем готовые файлы.
        }

        foreach (var source in fallbackSources)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(source, targetPath, overwrite: true);
            return true;
        }

        return false;
    }

    private static void Extract(string logicalName, string targetPath)
    {
        using var source = Open(logicalName)
            ?? throw new FileNotFoundException($"вшитого файла нет: {logicalName}");

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        // Совпал размер — считаем, что файл тот же. Сравнивать содержимое сорока
        // мегабайт на каждом запуске незачем: файл кладём мы сами, и меняется он
        // только вместе с версией клиента.
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == source.Length) return;

        using var target = File.Create(targetPath);
        source.CopyTo(target);
    }
}
