// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Json;
using Method.Core.Json;

namespace Method.Core.Storage;

/// <summary>
/// Как содержимое хранилища закрывается от посторонних. Реализация зависит от
/// платформы, поэтому её здесь нет: логика хранения проверяется тестами без
/// настоящего шифрования, а настоящее шифрование подставляет приложение.
/// </summary>
public interface ISecretBox
{
    byte[] Protect(byte[] plaintext);

    /// <summary>Расшифровать. Пусто — прочитать не вышло (чужой ключ, порча файла).</summary>
    byte[]? Unprotect(byte[] ciphertext);
}

/// <summary>
/// Профили и подписки на диске.
///
/// <para><b>Почему зашифровано.</b> В профилях лежат пароли, ключи Reality и
/// секреты обфускации от серверов человека. Файл в его же профиле пользователя
/// защищён правами, но права переживают не всё: скопированный на флешку каталог,
/// резервная копия, вынутый диск. Ключ здесь привязан к пользователю на этой
/// машине — унесённый файл не прочитается.</para>
///
/// <para><b>Почему запись через временный файл.</b> Прерванная запись на месте
/// оставляет обрезанный файл, а это для человека выглядит как «клиент потерял все
/// серверы». Пишем рядом и переименовываем: переименование атомарно, и в любой
/// момент на диске лежит либо старый целый файл, либо новый целый.</para>
/// </summary>
public sealed class ProfileStore
{
    private readonly string _directory;
    private readonly ISecretBox _box;

    public ProfileStore(string directory, ISecretBox box)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _box = box ?? throw new ArgumentNullException(nameof(box));
    }

    public static ProfileStore Default(ISecretBox box) => new(AppPaths.UserDataDir, box);

    private string ProfilesPath => Path.Combine(_directory, "profiles.dat");
    private string SubscriptionsPath => Path.Combine(_directory, "subscriptions.dat");

    public IReadOnlyList<StoredProfile> LoadProfiles() =>
        Read<List<StoredProfile>>(ProfilesPath) ?? [];

    public void SaveProfiles(IEnumerable<StoredProfile> profiles) =>
        Write(ProfilesPath, profiles.ToList());

    public IReadOnlyList<Subscription> LoadSubscriptions() =>
        Read<List<Subscription>>(SubscriptionsPath) ?? [];

    public void SaveSubscriptions(IEnumerable<Subscription> subscriptions) =>
        Write(SubscriptionsPath, subscriptions.ToList());

    private T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;

            var sealedBytes = File.ReadAllBytes(path);
            if (sealedBytes.Length == 0) return null;

            var plain = _box.Unprotect(sealedBytes);
            if (plain is null) return null;

            return JsonSerializer.Deserialize<T>(plain, MethodJson.Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Прочитать не вышло — отвечаем «ничего нет». Бросать отсюда нельзя:
            // единственный испорченный файл не должен мешать клиенту запуститься.
            // Затирать его тоже нельзя: пока файл цел, у него есть шанс на разбор.
            return null;
        }
    }

    private void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(_directory);

        var plain = JsonSerializer.SerializeToUtf8Bytes(value, MethodJson.Options);
        var sealedBytes = _box.Protect(plain);

        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, sealedBytes);
        File.Move(temporary, path, overwrite: true);
    }
}
