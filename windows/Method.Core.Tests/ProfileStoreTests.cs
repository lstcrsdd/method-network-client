// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text;
using Method.Core.Models;
using Method.Core.Storage;
using Xunit;

namespace Method.Core.Tests;

public class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "method-store-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSecretBox _box = new();

    private ProfileStore Store => new(_dir, _box);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* не критично */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Пустое_хранилище_читается_пустым_списком()
    {
        // Первый запуск: серверов нет, и это не ошибка.
        Assert.Empty(Store.LoadProfiles());
        Assert.Empty(Store.LoadSubscriptions());
    }

    [Fact]
    public void Профили_переживают_запись_и_чтение()
    {
        var profiles = new[]
        {
            new StoredProfile { Profile = TestProfiles.Hysteria2() },
            new StoredProfile { Profile = TestProfiles.VlessGrpc(), SubscriptionId = Guid.NewGuid() },
        };

        Store.SaveProfiles(profiles);
        var restored = Store.LoadProfiles();

        Assert.Equal(profiles.Length, restored.Count);
        Assert.Equal(profiles[0].Profile, restored[0].Profile);
        Assert.Equal(profiles[1].SubscriptionId, restored[1].SubscriptionId);
    }

    [Fact]
    public void Пароли_не_лежат_на_диске_открытым_текстом()
    {
        // Ради этого хранилище и шифруется: файл переживает копирование на флешку
        // и попадание в резервную копию, а права — нет.
        Store.SaveProfiles([new StoredProfile { Profile = TestProfiles.Hysteria2() }]);

        var raw = File.ReadAllBytes(Path.Combine(_dir, "profiles.dat"));
        Assert.DoesNotContain("shared:s3cret", Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public void Подписки_переживают_запись_и_чтение()
    {
        var subscription = new Subscription
        {
            Url = "https://sub.example.com/list",
            Name = "Моя подписка",
            Title = "Example",
            UpdateIntervalHours = 12,
            LastSkippedCount = 2,
        };

        Store.SaveSubscriptions([subscription]);
        var restored = Assert.Single(Store.LoadSubscriptions());

        Assert.Equal(subscription.Url, restored.Url);
        Assert.Equal(subscription.Title, restored.Title);
        Assert.Equal(12, restored.UpdateIntervalHours);
        Assert.Equal(2, restored.LastSkippedCount);
    }

    [Fact]
    public void Испорченный_файл_не_мешает_клиенту_запуститься()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "profiles.dat"), [1, 2, 3, 4, 5]);

        // Единственный испорченный файл не должен ронять запуск. И затирать его
        // нельзя: пока он цел, у него есть шанс на разбор.
        Assert.Empty(Store.LoadProfiles());
        Assert.True(File.Exists(Path.Combine(_dir, "profiles.dat")));
    }

    [Fact]
    public void Файл_от_чужого_ключа_читается_как_пустой()
    {
        Store.SaveProfiles([new StoredProfile { Profile = TestProfiles.Hysteria2() }]);

        // Другой пользователь или другая машина: расшифровать нечем.
        var foreign = new ProfileStore(_dir, new FakeSecretBox(key: 0xAB));
        Assert.Empty(foreign.LoadProfiles());
    }

    [Fact]
    public void Прерванная_запись_не_рвёт_прежние_данные()
    {
        Store.SaveProfiles([new StoredProfile { Profile = TestProfiles.Hysteria2() }]);

        // Обломок временного файла от прерванной записи не должен ни читаться, ни
        // мешать: настоящий файл на месте и цел.
        File.WriteAllBytes(Path.Combine(_dir, "profiles.dat.tmp"), [0, 0, 0]);

        var restored = Assert.Single(Store.LoadProfiles());
        Assert.Equal("US East", restored.Profile.Name);
    }

    [Fact]
    public void Повторная_запись_заменяет_прежнее_целиком()
    {
        Store.SaveProfiles([new StoredProfile { Profile = TestProfiles.Hysteria2() }]);
        Store.SaveProfiles([new StoredProfile { Profile = TestProfiles.Shadowsocks() }]);

        var restored = Assert.Single(Store.LoadProfiles());
        Assert.Equal(VpnProtocol.Shadowsocks, restored.Profile.Protocol);
    }

    /// <summary>
    /// Подмена настоящего шифрования: тот же контракт, обратимое преобразование.
    /// Логика хранения проверяется без привязки к платформе, а настоящий ключ
    /// подставляет приложение.
    /// </summary>
    private sealed class FakeSecretBox(byte key = 0x5A) : ISecretBox
    {
        public byte[] Protect(byte[] plaintext) => Xor(plaintext);

        public byte[]? Unprotect(byte[] ciphertext)
        {
            var plain = Xor(ciphertext);
            // Признак «расшифровано нашим ключом»: настоящий контейнер это тоже
            // умеет, и без такой проверки чужой файл разобрался бы в мусор.
            return plain.Length > 0 && plain[0] is (byte)'[' or (byte)'{' ? plain : null;
        }

        private byte[] Xor(byte[] data)
        {
            var result = new byte[data.Length];
            for (var i = 0; i < data.Length; i++) result[i] = (byte)(data[i] ^ key);
            return result;
        }
    }
}
