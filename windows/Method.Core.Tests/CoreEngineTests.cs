// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Native;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Движок решений общего ядра.
///
/// <para>Проверки идут против настоящей библиотеки: смысл ядра в том, что
/// логику выбора маршрута не переписывают на каждой платформе, и заглушка
/// проверяла бы нашу же выдумку вместо неё. Без собранной библиотеки проверки
/// пропускаются — клиент собирается и без ядра.</para>
/// </summary>
public class CoreEngineTests
{
    /// <summary>Два маршрута на разных осях: на одной оси запасного не бывает.</summary>
    private static CoreEngine WithCatalog(out uint lane, out uint quic, out uint tls)
    {
        var engine = new CoreEngine(TimeSpan.FromSeconds(10));

        quic = engine.AddRoute(new CoreRoute("fi.hysteria2", "fi", "hysteria2", "FI", CoreAxis.QuicUdp));
        tls = engine.AddRoute(new CoreRoute("lt.grpc", "lt", "vless-grpc", "LT", CoreAxis.FakeTlsH2));
        lane = engine.AddLane(new CoreLane("web", "Весь трафик"));

        return engine;
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

    [SkippableFact]
    public void Каталог_принимается_и_выдаёт_дескрипторы()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        using var engine = WithCatalog(out var lane, out var quic, out var tls);

        // Ноль дескриптором не бывает — по нему и отличают «не нашлось».
        Assert.NotEqual(0u, lane);
        Assert.NotEqual(0u, quic);
        Assert.NotEqual(0u, tls);
        Assert.NotEqual(quic, tls);
    }

    [SkippableFact]
    public void По_замерам_ядро_ставит_маршрут_на_полосу()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        using var engine = WithCatalog(out var lane, out var quic, out var tls);

        // Кормим ядро так, как это делал бы измерительный контур. Замеров
        // нарочно много: ядро не доверяет короткой серии и держит уверенность
        // низкой, пока не наберётся статистика, — иначе выбор делался бы по
        // случайному всплеску.
        ulong now = 0;
        for (var i = 0; i < 60; i++)
        {
            now += 10_000;
            engine.Observe(quic, CoreOutcome.Ok, 40, now);
            engine.Observe(tls, CoreOutcome.Ok, 180, now);
        }

        var actions = engine.Reconcile(now);

        // Полоса пуста, значит движок обязан предложить, что на неё поставить.
        Assert.Contains(actions, a => a.Kind == CoreActionKind.Select && a.Lane == lane);
    }

    [SkippableFact]
    public void Решение_объясняется_словами()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        using var engine = WithCatalog(out _, out var quic, out var tls);

        ulong now = 0;
        for (var i = 0; i < 60; i++)
        {
            now += 10_000;
            engine.Observe(quic, CoreOutcome.Ok, 40, now);
            engine.Observe(tls, CoreOutcome.Ok, 180, now);
        }

        var actions = engine.Reconcile(now);

        // Без объяснения автоматика читается как своеволие, и её выключают.
        Assert.All(actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason)));
    }

    [SkippableFact]
    public void Пока_применение_не_подтверждено_полоса_пуста()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        using var engine = WithCatalog(out var lane, out var quic, out var tls);

        ulong now = 0;
        for (var i = 0; i < 60; i++)
        {
            now += 10_000;
            engine.Observe(quic, CoreOutcome.Ok, 40, now);
            engine.Observe(tls, CoreOutcome.Ok, 180, now);
        }

        engine.Reconcile(now);

        // Движок ничего не считает исполненным сам: пока исполнитель не сказал
        // «поставил», полоса для него без маршрута.
        Assert.Equal(0u, engine.LaneCurrent(lane));

        var chosen = engine.Reconcile(now)
            .First(a => a.Kind == CoreActionKind.Select && a.Lane == lane).Route;

        engine.LaneApplied(lane, chosen);

        Assert.Equal(chosen, engine.LaneCurrent(lane));
    }

    [SkippableFact]
    public void История_переносится_между_запусками()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        // Ради этого перенос и делается. Без него каждый запуск клиента даёт
        // несколько минут, когда все маршруты одинаково недостоверны и выбор
        // фактически случаен: ядро набирает уверенность десятками проб.
        byte[] saved;
        ulong now = 0;

        using (var first = WithCatalog(out _, out var quic, out var tls))
        {
            for (var i = 0; i < 60; i++)
            {
                now += 10_000;
                first.Observe(quic, CoreOutcome.Ok, 40, now);
                first.Observe(tls, CoreOutcome.Ok, 180, now);
            }

            Assert.True(first.Score(quic, CoreSla.Browse, now).Confidence > 0.3f,
                "за шестьдесят проб уверенность так и не набралась — проверять нечего");

            saved = first.SaveState(now);
        }

        Assert.NotEmpty(saved);

        using var second = WithCatalog(out _, out var freshQuic, out _);

        // Новый движок ничего не знает: уверенность нулевая.
        Assert.Equal(0f, second.Score(freshQuic, CoreSla.Browse, now).Confidence);

        // Прошла минута настоящего времени. Число это ядро не выдумывает и часов
        // не читает принципиально — спрашивает у нас, иначе замер прошлой недели
        // выглядел бы свежим.
        second.LoadState(saved, now, 60_000);
        second.Observe(freshQuic, CoreOutcome.Ok, 40, now);

        Assert.True(second.Score(freshQuic, CoreSla.Browse, now).Confidence > 0.3f,
            "история не перенеслась: после загрузки уверенность осталась низкой");
    }

    [SkippableFact]
    public void Пустое_состояние_не_ломает_движок()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        // Первый запуск после установки: сохранённого нет. Это обычное дело, а
        // не поломка, и падать здесь клиент не должен.
        using var engine = WithCatalog(out _, out var quic, out _);

        engine.LoadState([], 1_000, 0);
        engine.Observe(quic, CoreOutcome.Ok, 40, 1_000);

        Assert.Equal(0u, engine.LaneCurrent(1));
    }

    [SkippableFact]
    public void Ручное_закрепление_держит_маршрут()
    {
        Skip.IfNot(Available(), "библиотека ядра не собрана");

        using var engine = WithCatalog(out var lane, out var quic, out var tls);

        // Медленный маршрут, закреплённый человеком: выбор человека сильнее
        // любой математики — ровно то, ради чего сценарии стоят выше автоматики.
        ulong now = 0;
        for (var i = 0; i < 60; i++)
        {
            now += 10_000;
            engine.Observe(quic, CoreOutcome.Ok, 40, now);
            engine.Observe(tls, CoreOutcome.Ok, 180, now);
        }

        engine.LanePin(lane, tls, now + 600_000);

        var select = engine.Reconcile(now).FirstOrDefault(a => a.Kind == CoreActionKind.Select && a.Lane == lane);

        Assert.NotNull(select);
        Assert.Equal(tls, select.Route);
    }
}
