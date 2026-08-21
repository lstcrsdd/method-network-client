// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

namespace Method.Core;

/// <summary>Пути клиента на Windows.</summary>
public static class AppPaths
{
    public const string ProductName = "Method";

    /// <summary>
    /// Общий каталог: сюда извлекается движок и сюда же привилегированная часть
    /// пишет конфигурацию и свой журнал. Права на него урезаются при установке —
    /// каталог, из которого SYSTEM запускает исполняемый файл, обязан быть
    /// недоступен на запись обычному пользователю, иначе это готовое повышение
    /// привилегий.
    /// </summary>
    public static string ProgramDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    /// <summary>Конфигурация sing-box, которую запускает привилегированная часть.</summary>
    public static string ConfigFile => Path.Combine(ProgramDataDir, "config.json");

    /// <summary>Движок в установленном виде.</summary>
    public static string EngineFile => Path.Combine(ProgramDataDir, "sing-box.exe");

    public static string HelperLogFile => Path.Combine(ProgramDataDir, "helper.log");

    /// <summary>
    /// Данные человека: профили, подписки, настройки. Локальный каталог, а не
    /// перемещаемый: содержимое шифруется ключом текущего пользователя на этой
    /// машине, и уехав на другую машину вместе с профилем, оно там не
    /// расшифруется — а выглядеть это будет как «клиент потерял все серверы».
    /// </summary>
    public static string UserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    /// <summary>
    /// Найти движок: сначала установленный, потом рядом с исполняемым файлом,
    /// потом в исходниках (для запуска из-под отладчика).
    /// </summary>
    public static string ResolveEngine()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            EngineFile,
            Path.Combine(baseDir, "sing-box.exe"),
            Path.Combine(baseDir, "Resources", "sing-box.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "Resources", "sing-box.exe"),
        ];

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }

        // Ничего не нашли — возвращаем ожидаемый путь, чтобы сообщение об ошибке
        // называло место, куда движок должен был лечь.
        return EngineFile;
    }
}
