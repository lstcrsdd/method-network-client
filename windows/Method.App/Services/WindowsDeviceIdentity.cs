// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Method.Core;
using Method.Core.Subscriptions;
using Microsoft.Win32;

namespace Method.App.Services;

/// <summary>
/// Идентификатор машины для панели подписок.
///
/// <para>Берётся тот, что уже есть у самой Windows и переживает переустановку
/// клиента: свой сгенерированный жил бы в наших файлах, и человек, переставивший
/// клиент, выглядел бы для панели новым устройством — а лимит устройств у панелей
/// как раз и считается по нему.</para>
///
/// <para>Если прочитать его не удалось, заводим свой и запоминаем: меняющийся от
/// запуска к запуску идентификатор для панели хуже чужого — он расходует лимит
/// устройств на каждом запуске.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceIdentity : IDeviceIdentity
{
    private static readonly Lazy<string> Value = new(Resolve);

    public string Hwid => Value.Value;

    public string ShortHwid => Hwid.Length <= 12 ? Hwid : Hwid[..12];

    public string HashedHwid =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"method:{Hwid}"))).ToLowerInvariant();

    public string DeviceName => Environment.MachineName;

    public string Platform => "Windows";

    private static string Resolve() => (MachineGuid() ?? StoredFallback()).ToUpperInvariant();

    private static string? MachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var value = key?.GetValue("MachineGuid") as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string StoredFallback()
    {
        var path = Path.Combine(AppPaths.UserDataDir, "device.id");

        try
        {
            if (File.Exists(path))
            {
                var stored = File.ReadAllText(path).Trim();
                if (stored.Length > 0) return stored;
            }

            var generated = Guid.NewGuid().ToString();
            Directory.CreateDirectory(AppPaths.UserDataDir);
            File.WriteAllText(path, generated);
            return generated;
        }
        catch (IOException)
        {
            // Записать не вышло — отдаём разовый. Панель посчитает это новым
            // устройством, но это лучше, чем отказ загрузить подписку вовсе.
            return Guid.NewGuid().ToString();
        }
    }
}
