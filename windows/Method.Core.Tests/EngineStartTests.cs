// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using Method.Core.Core;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Движок не просто принимает конфигурацию, а поднимается с ней.
///
/// <para>Это ровно та проверка, которой не хватает <c>sing-box check</c>: он
/// разбирает конфигурацию и инициализирует outbound, но часть отказов — например,
/// незнакомое значение стека TUN — вылезает только при запуске. Раньше о таких
/// вещах узнавали на живой машине по FATAL при подключении.</para>
///
/// <para>Берётся пробная конфигурация: mixed-инбаунд на петле, без адаптера и без
/// маршрутов. Она ничего не меняет в сети машины — поднимается локальный слушатель
/// и через пару секунд гасится. Полную конфигурацию туннеля так не проверить: она
/// требует прав и создаёт адаптер, то есть меняет сеть у того, кто гоняет тесты.</para>
/// </summary>
public class EngineStartTests
{
    /// <summary>Сколько ждём, прежде чем считать, что движок поднялся.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(2500);

    public static TheoryData<string, int> ВсеПротоколы()
    {
        var data = new TheoryData<string, int>();
        var port = 18101;
        foreach (var profile in TestProfiles.All()) data.Add(profile.Name, port++);
        return data;
    }

    [Theory]
    [MemberData(nameof(ВсеПротоколы))]
    public async Task Движок_поднимается_с_нашей_конфигурацией(string profileName, int listenPort)
    {
        var profile = TestProfiles.All().First(p => p.Name == profileName);
        var json = SingBoxConfigBuilder.BuildProbeJson(profile, listenPort);

        var path = Path.Combine(Path.GetTempPath(), $"method-probe-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);

        using var process = Process.Start(new ProcessStartInfo(EngineLocator.Path, ["run", "-c", path])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("движок не запустился");

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            // Ждём по-простому: если движок жив, потоки вывода не закроются до его
            // смерти, и любое ожидание их чтения на успешном пути — вечное. Именно
            // так этот тест и завис в первый раз: текст для Assert вычислялся всегда,
            // а он ждал stderr, который ждал завершения процесса, который ждал Kill
            // в finally.
            var died = process.WaitForExit((int)Settle.TotalMilliseconds);
            if (!died) return;

            var exitCode = process.ExitCode;
            var details = (await stderr).Trim() + Environment.NewLine + (await stdout).Trim();

            Assert.Fail(
                $"движок умер на конфигурации «{profileName}» (код {exitCode}).{Environment.NewLine}" +
                $"{details}{Environment.NewLine}Конфигурация:{Environment.NewLine}{json}");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException) { /* уже завершился */ }

            try { File.Delete(path); } catch (IOException) { /* не критично */ }
        }
    }
}

/// <summary>
/// Путь к движку. Его нет в репозитории — приносит Scripts\bootstrap.ps1.
/// Отсутствие движка не пропускается молча: проверка, которую «пропустили», в
/// отчёте неотличима от пройденной, а цена ошибки здесь — неподнявшийся туннель
/// у всех сразу.
/// </summary>
internal static class EngineLocator
{
    public static string Path => _path ??= Find();
    private static string? _path;

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "Resources", "sing-box.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Движка нет: Resources\\sing-box.exe не найден ни в одном родительском каталоге. " +
            "Запусти Scripts\\bootstrap.ps1 — без движка проверять нечем.");
    }
}
