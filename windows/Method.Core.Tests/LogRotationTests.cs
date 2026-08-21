// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text;
using Method.Core.Tunnel;
using Xunit;

namespace Method.Core.Tests;

public class LogRotationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"method-log-{Guid.NewGuid():N}.log");
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { /* не критично */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Короткий_журнал_не_трогается()
    {
        // Когда резать нечего, обрезка обязана обойтись проверкой длины файла.
        var original = Lines(Now, 10, "запись");
        File.WriteAllText(_path, original);

        LogRotation.TrimIfNeeded(_path, Now);

        Assert.Equal(original, File.ReadAllText(_path));
    }

    [Fact]
    public void Записи_за_срок_хранения_переживают_обрезку()
    {
        // Час назад — внутри трёхчасового срока, значит остаётся.
        var text = Lines(Now - TimeSpan.FromHours(20), 40_000, "старое")
                 + Lines(Now - TimeSpan.FromHours(1), 40_000, "свежее");
        File.WriteAllText(_path, text);

        LogRotation.TrimIfNeeded(_path, Now, maxBytes: 64, keepBytes: 1024, maxKeepBytes: 4L * 1024 * 1024);

        var result = File.ReadAllText(_path);
        Assert.Contains("свежее", result);
        Assert.StartsWith(LogRotation.TrimMarker, result);
    }

    [Fact]
    public void Остаток_не_превышает_потолок()
    {
        // Иначе следующая же строка снова запустит чтение и перезапись всего файла —
        // и так на каждую строку, то есть помощник встанет колом под нагрузкой.
        var text = Lines(Now, 20_000, "свежее");
        File.WriteAllText(_path, text);

        LogRotation.TrimIfNeeded(_path, Now, maxBytes: 1024, keepBytes: 0, maxKeepBytes: 8 * 1024);

        // Ровно потолок, без поблажек: записи на русском, и в UTF-8 они вдвое
        // тяжелее, чем длина строки в символах. Считать надо байты.
        Assert.True(
            new FileInfo(_path).Length <= 8 * 1024,
            $"после обрезки осталось {new FileInfo(_path).Length} байт при потолке 8 КБ");
    }

    [Fact]
    public void Малое_число_свежих_записей_добирается_старыми()
    {
        // Выбрасывать полезное впустую незачем: если за срок хранения записей мало,
        // остаток добирается более старыми.
        var text = Lines(Now - TimeSpan.FromHours(20), 5_000, "старое")
                 + Lines(Now - TimeSpan.FromMinutes(1), 3, "свежее");
        File.WriteAllText(_path, text);

        LogRotation.TrimIfNeeded(_path, Now, maxBytes: 1024, keepBytes: 32 * 1024, maxKeepBytes: 1024 * 1024);

        var result = File.ReadAllText(_path);
        Assert.Contains("старое", result);
        Assert.Contains("свежее", result);
    }

    [Fact]
    public void Обрезанный_журнал_помечен()
    {
        // Иначе по журналу нельзя отличить «событий не было» от «записи вытеснены».
        File.WriteAllText(_path, Lines(Now, 20_000, "запись"));

        LogRotation.TrimIfNeeded(_path, Now, maxBytes: 1024, keepBytes: 0, maxKeepBytes: 8 * 1024);

        Assert.StartsWith(LogRotation.TrimMarker, File.ReadAllText(_path));
    }

    [Fact]
    public void Журнал_остаётся_читаемым_после_обрезки()
    {
        // Ровные строки с меткой времени — по ним потом и разбирают происходившее.
        File.WriteAllText(_path, Lines(Now, 20_000, "запись"));

        LogRotation.TrimIfNeeded(_path, Now, maxBytes: 1024, keepBytes: 0, maxKeepBytes: 8 * 1024);

        var lines = File.ReadAllLines(_path);
        Assert.All(lines.Skip(1), line => Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", line));
    }

    private static string Lines(DateTimeOffset start, int count, string tag)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
            sb.Append($"{start.AddMilliseconds(i):O} {tag} {i}\n");
        return sb.ToString();
    }
}
