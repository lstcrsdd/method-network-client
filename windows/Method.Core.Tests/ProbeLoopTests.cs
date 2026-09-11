// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Collections.Concurrent;
using Method.Core.Core;
using Method.Core.Native;
using Method.Core.Services;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Измерительный контур: что именно контур скармливает ядру.
///
/// <para>Задержки здесь заданные, а не живые. Смысл проверок — в том, как
/// контур обходится с молчанием маршрута и как накопленное отражается на
/// уверенности ядра; на живых серверах это проверялось бы вперемешку с их
/// собственным состоянием и меняло бы исход от прогона к прогону.</para>
/// </summary>
public class ProbeLoopTests
{
    /// <summary>Задержки по расписанию: что скажут, то маршрут и «ответит».</summary>
    private sealed class Dictated(IReadOnlyDictionary<string, int?> answers) : IRouteDelaySource
    {
        public ConcurrentBag<string> Asked { get; } = [];

        public Task<int?> DelayAsync(string route, CancellationToken ct = default)
        {
            Asked.Add(route);
            return Task.FromResult(answers.TryGetValue(route, out var delay) ? delay : null);
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

    private static (CoreEngine Engine, ProbeTarget[] Targets) Catalog()
    {
        var engine = new CoreEngine(ProbeLoop.SteadyStep);

        var quic = engine.AddRoute(new CoreRoute("fi.hysteria2", "fi", "hysteria2", "FI", CoreAxis.QuicUdp));
        var tls = engine.AddRoute(new CoreRoute("lt.grpc", "lt", "vless-grpc", "LT", CoreAxis.FakeTlsH2));
        engine.AddLane(new CoreLane("web", "Весь трафик"));

        return (engine, [new ProbeTarget("proxy", quic), new ProbeTarget("lane-1", tls)]);
    }

    [SkippableFact]
    public async Task Круг_обходит_все_маршруты()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, targets) = Catalog();
        using var _ = engine;

        var source = new Dictated(new Dictionary<string, int?> { ["proxy"] = 40, ["lane-1"] = 180 });
        var round = await new ProbeLoop(source, engine, targets).RoundAsync(CancellationToken.None);

        Assert.Equal(["lane-1", "proxy"], source.Asked.Order());
        Assert.Equal(2, round.Alive);
        Assert.Equal(2, round.Total);
    }

    [SkippableFact]
    public async Task Молчание_маршрута_подаётся_как_отказ()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, targets) = Catalog();
        using var _ = engine;

        // Быстрый, но мёртвый маршрут — самый опасный случай. Пропусти его
        // молчание, и у ядра он останется с прежней хорошей оценкой: оно сочтёт
        // его лучшим и поставит полосе.
        var source = new Dictated(new Dictionary<string, int?> { ["proxy"] = 20, ["lane-1"] = 200 });
        var loop = new ProbeLoop(source, engine, targets);

        for (var i = 0; i < 40; i++) await loop.RoundAsync(CancellationToken.None);

        var alive = engine.Score(targets[0].Route, CoreSla.Browse, (ulong)Environment.TickCount64);

        var dead = new Dictated(new Dictionary<string, int?> { ["proxy"] = null, ["lane-1"] = 200 });
        var mourning = new ProbeLoop(dead, engine, targets);

        for (var i = 0; i < 40; i++) await mourning.RoundAsync(CancellationToken.None);

        var after = engine.Score(targets[0].Route, CoreSla.Browse, (ulong)Environment.TickCount64);

        Assert.True(after.Value < alive.Value,
            $"замолчавший маршрут не потерял в оценке: было {alive.Value}, стало {after.Value}");
    }

    [SkippableFact]
    public async Task Уверенность_набирается_замерами_а_не_временем()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, targets) = Catalog();
        using var _ = engine;

        var source = new Dictated(new Dictionary<string, int?> { ["proxy"] = 40, ["lane-1"] = 180 });
        var loop = new ProbeLoop(source, engine, targets);

        var first = await loop.RoundAsync(CancellationToken.None);

        // На одном круге ядро не доверяет никому — и это его правило, а не наше.
        Assert.True(first.WarmingUp);

        for (var i = 0; i < 60; i++) await loop.RoundAsync(CancellationToken.None);

        var later = await loop.RoundAsync(CancellationToken.None);

        Assert.True(later.Confidence > first.Confidence,
            $"уверенность не выросла за 60 кругов: было {first.Confidence}, стало {later.Confidence}");
    }

    [SkippableFact]
    public async Task Разогрев_кончается_один_раз_и_сам_не_возвращается()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, targets) = Catalog();
        using var _ = engine;

        var source = new Dictated(new Dictionary<string, int?> { ["proxy"] = 40, ["lane-1"] = 180 });
        var loop = new ProbeLoop(source, engine, targets);

        ProbeRound round;
        var circles = 0;

        do { round = await loop.RoundAsync(CancellationToken.None); }
        while (round.WarmingUp && ++circles < 200);

        Assert.False(round.WarmingUp, "разогрев не кончился за 200 кругов");

        // В поддержании замеры стареют между кругами, и уверенность проседает
        // сама собой. Считай режим заново каждый раз — контур запрыгал бы между
        // частым и редким опросом без конца, а частый опрос по всем узлам это
        // ровно тот маячок, которого мы избегаем.
        for (var i = 0; i < 5; i++)
            Assert.False((await loop.RoundAsync(CancellationToken.None)).WarmingUp);
    }

    [SkippableFact]
    public async Task Смена_сети_возвращает_разогрев()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, targets) = Catalog();
        using var _ = engine;

        var source = new Dictated(new Dictionary<string, int?> { ["proxy"] = 40, ["lane-1"] = 180 });
        var loop = new ProbeLoop(source, engine, targets);

        ProbeRound round;
        var circles = 0;

        do { round = await loop.RoundAsync(CancellationToken.None); }
        while (round.WarmingUp && ++circles < 200);

        Assert.False(round.WarmingUp);

        // Ушли с Wi-Fi на провод: накопленное больше не про эту сеть. Набирать
        // историю надо заново и быстро — иначе выбор первые полчаса делается по
        // задержкам канала, которого уже нет.
        loop.NoteNetworkChanged();

        Assert.True((await loop.RoundAsync(CancellationToken.None)).WarmingUp);
    }

    [SkippableFact]
    public async Task Мёртвая_сеть_не_роняет_контур()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, targets) = Catalog();
        using var _ = engine;

        // Ни один маршрут не отвечает — обычное дело в поезде или при отключении
        // сети. Контур обязан пережить это и продолжить мерить: клиент, тихо
        // переставший следить за маршрутами, не восстановится и после того, как
        // сеть вернётся.
        var source = new Dictated(new Dictionary<string, int?>());
        var round = await new ProbeLoop(source, engine, targets).RoundAsync(CancellationToken.None);

        Assert.Equal(0, round.Alive);
        Assert.Equal(2, round.Total);
    }

    [SkippableFact]
    public async Task Применение_идёт_внутри_круга_а_не_рядом_с_ним()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, targets) = Catalog();
        using var _ = engine;

        var source = new Dictated(new Dictionary<string, int?> { ["proxy"] = 40, ["lane-1"] = 180 });

        using var stop = new CancellationTokenSource();
        var applied = 0;
        var inside = false;

        var loop = new ProbeLoop(source, engine, targets)
        {
            AfterRound = async (_, _) =>
            {
                // Круг обязан ждать применения. Вынеси его в подписчика события —
                // и остановка успеет освободить память ядра из-под работающего
                // действия: это падение клиента без единой строки в журнале.
                inside = true;
                await Task.Delay(30);

                if (++applied >= 2) await stop.CancelAsync();
                inside = false;
            },
        };

        await loop.RunAsync(stop.Token);

        Assert.Equal(2, applied);
        Assert.False(inside, "контур ушёл, не дождавшись применения");
    }

    [SkippableFact]
    public async Task Остановка_прекращает_круги()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        var (engine, targets) = Catalog();
        using var _ = engine;

        var source = new Dictated(new Dictionary<string, int?> { ["proxy"] = 40, ["lane-1"] = 180 });
        var loop = new ProbeLoop(source, engine, targets);

        using var stop = new CancellationTokenSource();
        var rounds = 0;
        loop.RoundDone += _ =>
        {
            // Останавливаем на первом же круге: контур обязан уйти по отмене, а
            // не доработать до конца разогрева. Туннель опускают в любой момент.
            if (Interlocked.Increment(ref rounds) >= 1) stop.Cancel();
        };

        await loop.RunAsync(stop.Token);

        Assert.Equal(1, rounds);
    }
}
