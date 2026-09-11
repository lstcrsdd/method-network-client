// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Tunnel;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Журнал привилегированной части не должен становиться хроникой того, чем
/// человек пользуется. Проверяем именно это: файл уходит в чужие руки при первом
/// же разборе неполадки, и полный путь к программе там — уже утечка.
/// </summary>
public class HelperLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"method-helper-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { /* не критично */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Путь_к_программе_остаётся_одним_именем()
    {
        var log = new HelperLog(_path);

        log.WriteEngine(
            @"INFO router: found process path: C:\Users\ivan\AppData\Local\Discord\app-1.0.9256\Discord.exe");

        var written = File.ReadAllText(_path);

        Assert.Contains("Discord.exe", written, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\ivan", written, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Несколько_путей_в_строке_чистятся_все()
    {
        var log = new HelperLog(_path);

        log.WriteEngine(@"копирование C:\Program Files\Method\sing-box.exe в D:\врем\sing-box.exe");

        var written = File.ReadAllText(_path);

        Assert.DoesNotContain(@"C:\Program", written, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\врем", written, StringComparison.Ordinal);
        Assert.Contains("sing-box.exe", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Обычный_текст_не_страдает()
    {
        // Чистка не должна съедать осмысленные записи: адреса, коды, время.
        var log = new HelperLog(_path);

        log.WriteEngine("outbound/hysteria2[proxy]: outbound connection to 149.154.167.41:443");

        Assert.Contains(
            "outbound connection to 149.154.167.41:443", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void Наши_записи_не_чистятся()
    {
        // Свои строки мы пишем сами и путей в них не кладём; трогать их незачем,
        // а путь к конфигурации в сообщении об ошибке — как раз то, что нужно
        // видеть при разборе.
        var log = new HelperLog(_path);

        log.Write(@"конфигурация записана: C:\ProgramData\Method\config.json");

        Assert.Contains(@"C:\ProgramData\Method\config.json", File.ReadAllText(_path), StringComparison.Ordinal);
    }
}
