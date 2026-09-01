// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Method.App.Services;

/// <summary>
/// Запуск интерфейса при входе в систему.
///
/// <para>Запись делается для текущего пользователя, а не для всей машины: прав
/// администратора на это не нужно, и настройка одного человека не меняет поведение
/// у остальных на общей машине.</para>
///
/// <para>Речь только об интерфейсе. Привилегированную часть поднимает своя
/// служба, и от этой настройки она не зависит — иначе выключенный
/// автозапуск окна оставлял бы машину без туннеля после перезагрузки.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Method";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Включить или выключить. Пусто — вышло; иначе причина отказа.</summary>
    public static string? SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                            ?? throw new IOException("не удалось открыть раздел автозапуска");

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return "не удалось определить путь к самому себе";

            // Путь в кавычках: без них пробел в пути превращает одну команду в две,
            // и Windows пытается запустить «C:\Program» с аргументом.
            key.SetValue(ValueName, $"\"{exe}\"");
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
