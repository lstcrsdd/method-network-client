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
using Method.Core.Storage;

namespace Method.App.Services;

/// <summary>
/// Шифрование хранилища средствами Windows: ключ привязан к текущему пользователю
/// на этой машине.
///
/// <para>Своего ключа мы не заводим и не храним намеренно. Ключ, лежащий рядом с
/// данными, защищает ровно ни от чего, а спрашивать пароль при каждом запуске
/// клиента, который должен подниматься сам, — не тот размен. Здесь ключом
/// распоряжается система, и он не покидает машину: унесённый файл профилей на
/// другой машине не прочитается.</para>
///
/// <para>Дополнительная строка подмешивается в шифрование: файл, подсунутый от
/// другого приложения того же пользователя, не расшифруется как наш.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretBox : ISecretBox
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("network.method.client/storage/v1");

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[]? Unprotect(byte[] ciphertext)
    {
        try
        {
            return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Файл от другого пользователя, с другой машины или испорчен. Для
            // вызывающего это одно и то же: прочитать нечем.
            return null;
        }
    }
}
