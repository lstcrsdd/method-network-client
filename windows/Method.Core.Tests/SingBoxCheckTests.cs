// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using Method.Core.Core;
using Method.Core.Models;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Конфигурация проверяется настоящим движком, а не глазами.
///
/// Тесты выше сверяют форму: что в дереве лежит ожидаемое поле. Форму можно
/// собрать правильную по нашим представлениям и неправильную по схеме sing-box —
/// именно так и выходило раньше, а узнавали об этом на живой машине по FATAL при
/// старте. Здесь конфигурацию читает тот же бинарь, который потом будет её
/// исполнять, и той же версии.
///
/// Что <c>check</c> ловит, а что нет — замерено на 1.13.13, а не предположено:
/// <list type="bullet">
///   <item>ловит: битый JSON, неизвестный тип outbound, неполный outbound
///         (у hysteria2 без TLS — «TLS required»), незнакомые поля схемы;</item>
///   <item>не ловит: неизвестное значение <c>stack</c> у TUN и отсутствие
///         <c>default_domain_resolver</c> — и то и другое падает уже в рантайме.</item>
/// </list>
/// Поэтому <c>check</c> — необходимая проверка, но не достаточная: значение
/// стека сторожит собственная проверка сборщика, а полное доказательство даст
/// только запуск движка на живой машине.
/// </summary>
public class SingBoxCheckTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "method-singbox-check-" + Guid.NewGuid().ToString("N"));

    public SingBoxCheckTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); }
        catch (IOException) { /* временный каталог не критичен */ }
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string> ВсеПротоколы()
    {
        var data = new TheoryData<string>();
        foreach (var profile in TestProfiles.All()) data.Add(profile.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ВсеПротоколы))]
    public void Движок_принимает_конфигурацию_туннеля(string profileName)
    {
        var profile = TestProfiles.All().First(p => p.Name == profileName);
        var json = SingBoxConfigBuilder.BuildJson(profile, new SingBoxConfigBuilder.Options
        {
            ClashSecret = "проверка",
        });

        Check(json, profileName);
    }

    [Theory]
    [MemberData(nameof(ВсеПротоколы))]
    public void Движок_принимает_пробную_конфигурацию(string profileName)
    {
        var profile = TestProfiles.All().First(p => p.Name == profileName);
        Check(SingBoxConfigBuilder.BuildProbeJson(profile, 18080), profileName + " (проба)");
    }

    [Fact]
    public void Движок_принимает_переключаемую_группу()
    {
        // Форму селектора проверяем не глазами: поля «default» и
        // «interrupt_exist_connections» есть в документации, но принимает их
        // конкретная версия бинаря, а не документация. Ошибись здесь — и клиент
        // упадёт при старте туннеля, когда человек включит автоматику.
        var profile = TestProfiles.Hysteria2();
        var other = TestProfiles.Hysteria2() with { Id = Guid.NewGuid(), Name = "Запасной", Host = "198.51.100.7" };

        var json = SingBoxConfigBuilder.BuildJson(profile, new SingBoxConfigBuilder.Options
        {
            ClashSecret = "проверка",
            Autopilot = true,
            AutoNodes = [other],
            CoreDriven = true,
        });

        Check(json, "переключаемая группа");
    }

    private void Check(string json, string label)
    {
        var path = Path.Combine(_workDir, "config.json");
        File.WriteAllText(path, json);

        var psi = new ProcessStartInfo(EngineLocator.Path, ["check", "-c", path])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("sing-box не запустился");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"sing-box check завис на конфигурации «{label}»");
        }

        Assert.True(
            process.ExitCode == 0,
            $"sing-box check отверг конфигурацию «{label}» (код {process.ExitCode}).\n" +
            $"{stderr}{stdout}\n" +
            "Конфигурация:\n" + json);
    }

}
