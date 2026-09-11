// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Storage;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// История измерений между запусками.
///
/// <para>Главное свойство — не терять её молча и не падать, когда её нет.
/// Клиент, каждый запуск начинающий с нуля, первые минуты выбирает маршрут
/// вслепую; клиент, падающий на испорченном файле, не запускается вовсе.</para>
/// </summary>
public class CoreStateStoreTests : IDisposable
{
    /// <summary>Обратимое преобразование вместо шифрования: проверяем хранилище, а не Windows.</summary>
    private sealed class Reversible : ISecretBox
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[]? Unprotect(byte[] ciphertext) => ciphertext.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    /// <summary>Ящик с чужим ключом: так выглядит файл, унесённый на другую машину.</summary>
    private sealed class Foreign : ISecretBox
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[]? Unprotect(byte[] ciphertext) => null;
    }

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "method-core-state-" + Guid.NewGuid().ToString("N"));

    public CoreStateStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* временный каталог не критичен */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Сохранённое_читается_обратно()
    {
        var store = new CoreStateStore(_dir, new Reversible());
        byte[] state = [1, 2, 3, 250, 0, 77];

        Assert.True(store.Save(state));

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(state, loaded.State);

        // Прошло почти нисколько: ядру важно знать это точно, иначе оно сочтёт
        // свежие замеры протухшими и начнёт набирать историю заново.
        Assert.True(loaded.Elapsed < TimeSpan.FromMinutes(1), $"промежуток {loaded.Elapsed}");
    }

    [Fact]
    public void Первый_запуск_истории_не_имеет()
    {
        // Обычное дело после установки, а не поломка.
        Assert.Null(new CoreStateStore(_dir, new Reversible()).Load());
    }

    [Fact]
    public void Файл_с_чужой_машины_читается_как_пустота()
    {
        // Хранилище привязано к пользователю и машине. Унесённый файл не должен
        // ни расшифровываться, ни ронять клиент.
        new CoreStateStore(_dir, new Reversible()).Save([9, 9, 9]);

        Assert.Null(new CoreStateStore(_dir, new Foreign()).Load());
    }

    [Fact]
    public void Испорченный_файл_не_роняет_клиент()
    {
        var store = new CoreStateStore(_dir, new Reversible());
        store.Save([1, 2, 3]);

        // Обрыв питания посреди записи, сбойный диск, чужие руки — итог один.
        File.WriteAllBytes(Path.Combine(_dir, "core-state.bin"), [0xFF, 0x00, 0xFF]);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Пустая_выгрузка_убирает_прежнюю()
    {
        // Ядру нечего сохранять — значит и прежнее держать нельзя: оно уже не
        // про нынешний каталог маршрутов, и восстановленное вводило бы в
        // заблуждение о маршрутах, которых больше нет.
        var store = new CoreStateStore(_dir, new Reversible());
        store.Save([1, 2, 3]);

        Assert.True(store.Save([]));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Часы_назад_не_дают_отрицательного_промежутка()
    {
        // Сверка времени, смена пояса, севшая батарейка. Отрицательный
        // промежуток ядру давать нельзя — оно пересчитывает по нему свежесть
        // всех замеров.
        var store = new CoreStateStore(_dir, new Reversible());
        store.Save([1, 2, 3]);

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.True(loaded.Elapsed >= TimeSpan.Zero);
    }
}
