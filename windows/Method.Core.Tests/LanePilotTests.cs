// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Core;
using Method.Core.Native;
using Method.Core.Services;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Исполнение решений ядра.
///
/// <para>Проверяется не HTTP, а порядок: сделали — подтвердили, не сделали — не
/// подтвердили. Ошибка в этом порядке не падает и в журнале не видна — ядро
/// просто считает поставленным маршрут, которого на полосе нет, — а устроить
/// отказ движка на живом сервере ради проверки нельзя.</para>
/// </summary>
public class LanePilotTests
{
    /// <summary>Переключатель, который слушается или упирается — по указанию.</summary>
    private sealed class Knob(bool obeys) : ILaneSwitch
    {
        public List<string> Asked { get; } = [];

        public Task<bool> SelectAsync(string lane, string route, CancellationToken ct = default)
        {
            Asked.Add(route);
            return Task.FromResult(obeys);
        }
    }

    private static bool Available()
    {
        try
        {
            using var engine = new CoreEngine(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    /// <summary>Ядро с набранной историей: быстрый маршрут и медленный, оси разные.</summary>
    private static (CoreEngine Engine, uint Lane, Dictionary<uint, string> Tags) Ready()
    {
        var engine = new CoreEngine(ProbeLoop.SteadyStep);

        var quic = engine.AddRoute(new CoreRoute("fi.hysteria2", "fi", "hysteria2", "FI", CoreAxis.QuicUdp));
        var tls = engine.AddRoute(new CoreRoute("lt.grpc", "lt", "vless-grpc", "LT", CoreAxis.FakeTlsH2));
        var lane = engine.AddLane(new CoreLane("web", "Весь трафик"));

        // Замеры кладём на ту же шкалу времени, по которой потом спросят решение,
        // — на часы от запуска машины. Своя шкала, начатая с нуля, сделала бы всю
        // историю протухшей на десятки суток вперёд, и ядро ответило бы «живых
        // маршрутов нет», не разбираясь.
        var now = (ulong)Environment.TickCount64 - 600_000;

        for (var i = 0; i < 60; i++)
        {
            now += 10_000;
            engine.Observe(quic, CoreOutcome.Ok, 40, now);
            engine.Observe(tls, CoreOutcome.Ok, 180, now);
        }

        return (engine, lane, new Dictionary<uint, string> { [quic] = "proxy", [tls] = "lane-1" });
    }

    private static ProbeRound Round(bool warmingUp) => new(2, 2, warmingUp ? 0.1f : 0.9f, warmingUp);

    [SkippableFact]
    public async Task Без_замеров_полоса_остаётся_на_ручном_выборе()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        // Самое дорогое место всей автоматики. Ядро без замеров отвечает
        // буквально: «живых маршрутов нет — трафик блокируется». Исполни это
        // дословно — и клиент режет весь трафик первые минуты после каждого
        // подключения, притом на ровном месте: за полосой стоит узел, который
        // человек выбрал руками.
        using var engine = new CoreEngine(ProbeLoop.SteadyStep);

        var quic = engine.AddRoute(new CoreRoute("fi.hysteria2", "fi", "hysteria2", "FI", CoreAxis.QuicUdp));
        engine.AddLane(new CoreLane("web", "Весь трафик"));

        var knob = new Knob(obeys: true);
        var held = 0;

        var pilot = new LanePilot(
            engine, knob, "auto", new Dictionary<uint, string> { [quic] = "proxy" }, standing: "proxy");

        pilot.Held += _ => held++;

        await pilot.ApplyAsync(Round(warmingUp: true));

        Assert.Empty(knob.Asked);
        Assert.Equal(1, held);
        Assert.Equal("proxy", pilot.Standing);
    }

    [SkippableFact]
    public async Task Подтверждение_идёт_только_после_переключения()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, lane, tags) = Ready();
        using var _ = engine;

        var stubborn = new Knob(obeys: false);
        var pilot = new LanePilot(engine, stubborn, "auto", tags);

        await pilot.ApplyAsync(Round(warmingUp: false));

        Assert.NotEmpty(stubborn.Asked);

        // Движок отказал — для ядра полоса осталась пустой. Иначе оно считало бы
        // поставленным маршрут, которого там нет, и молчало бы об этом.
        Assert.Equal(0u, engine.LaneCurrent(lane));
    }

    [SkippableFact]
    public async Task После_переключения_ядро_знает_свой_маршрут()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, lane, tags) = Ready();
        using var _ = engine;

        var knob = new Knob(obeys: true);
        var pilot = new LanePilot(engine, knob, "auto", tags);

        LaneMove? moved = null;
        pilot.Moved += m => moved = m;

        await pilot.ApplyAsync(Round(warmingUp: false));

        Assert.NotEqual(0u, engine.LaneCurrent(lane));
        Assert.NotNull(moved);

        // Без объяснения автоматика читается как своеволие, и её выключают
        // первой — поэтому слова ядра доходят до человека, а не теряются.
        Assert.False(string.IsNullOrWhiteSpace(moved.Reason));

        // Но доходят без служебной части: ядро называет маршрут вместе с
        // опознавателем, а в ленте рядом уже стоит имя узла, и тридцать два
        // знака шестнадцатеричной каши только съедают её ширину.
        Assert.DoesNotMatch("[0-9a-fA-F]{32}", moved.Reason);
        Assert.Equal(tags[engine.LaneCurrent(lane)], moved.Tag);
    }

    [SkippableFact]
    public async Task Отказ_переключения_виден_и_не_считается_переездом()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, _, tags) = Ready();
        using var _ = engine;

        var pilot = new LanePilot(engine, new Knob(obeys: false), "auto", tags, standing: "proxy");

        var moved = 0;
        LaneMove? refused = null;

        pilot.Moved += _ => moved++;
        pilot.Refused += m => refused = m;

        await pilot.ApplyAsync(Round(warmingUp: false));

        Assert.Equal(0, moved);
        Assert.NotNull(refused);
        Assert.Equal("proxy", pilot.Standing);
    }

    [SkippableFact]
    public async Task Стоящий_маршрут_не_объявляется_переездом()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, _, tags) = Ready();
        using var _ = engine;

        // Ядро решило то, что и так стоит. Подтвердить — обязаны, а докладывать
        // человеку о переезде, которого не было, — нет: лента превратится в
        // поток одинаковых строк, и настоящий переезд в нём потеряется.
        var knob = new Knob(obeys: true);
        var first = new LanePilot(engine, knob, "auto", tags);

        await first.ApplyAsync(Round(warmingUp: false));

        var chosen = first.Standing;
        Assert.NotNull(chosen);

        var moved = 0;
        var again = new LanePilot(engine, knob, "auto", tags, standing: chosen);
        again.Moved += _ => moved++;

        await again.ApplyAsync(Round(warmingUp: false));

        Assert.Equal(0, moved);
    }

    [SkippableFact]
    public async Task Выбор_стоящего_маршрута_виден_как_решение()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, _, tags) = Ready();
        using var _ = engine;

        // Ядро выбрало узел человека — переезда нет, но решение есть. Без этого
        // строка прогрева в ленте висела бы «жду» до конца сеанса: снаружи
        // «ядро ещё не выбирает» и «выбрало то же самое» неотличимы.
        var pilot = new LanePilot(engine, new Knob(obeys: true), "auto", tags, standing: "proxy");

        var moved = 0;
        LaneMove? kept = null;

        pilot.Moved += _ => moved++;
        pilot.Kept += m => kept = m;

        await pilot.ApplyAsync(Round(warmingUp: false));

        Assert.Equal(0, moved);
        Assert.NotNull(kept);
        Assert.Equal("proxy", kept.Tag);
    }

    [SkippableFact]
    public async Task Закреплённый_человеком_маршрут_и_ставится()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, lane, tags) = Ready();
        using var _ = engine;

        // Ручной выбор сильнее любой математики: закреплён медленный маршрут —
        // ставится он, сколько бы быстрый ни выигрывал по замерам. Ради этого
        // автоматику и делали фоном, а не хозяином.
        var slow = tags.First(t => t.Value == "lane-1").Key;
        engine.LanePin(lane, slow, (ulong)Environment.TickCount64 + 600_000);

        var knob = new Knob(obeys: true);
        var pilot = new LanePilot(engine, knob, "auto", tags);

        await pilot.ApplyAsync(Round(warmingUp: false));

        Assert.Equal("lane-1", pilot.Standing);
    }
}
