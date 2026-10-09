// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Method.App.Services;

namespace Method.App.ViewModels;

/// <summary>
/// Что из узлов живо — без кнопки и без догадок.
///
/// <para><b>Зачем.</b> Проверка узлов запускалась только кнопкой. Пока её не нажали,
/// мёртвый узел в списке выглядел так же, как живой. На живой машине это кончилось
/// тем, что Hysteria2 молчала на всех узлах, а человек считал её рабочей: окно
/// показывало выбранную литовскую Hysteria, трафик шёл через финский Reality, и
/// ничто не говорило, что Hysteria мертва. Теперь клиент проверяет сам и говорит
/// прямо: какой узел не проходит, какой протокол не проходит нигде.</para>
///
/// <para>Два источника правды, и они не спорят. Фоновая проверка мерит все узлы
/// настоящим протоколом мимо туннеля. Сторож проходимости мерит выбранный узел
/// самим трафиком — это свежее и точнее, поэтому его вердикт ложится поверх.</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Как часто перепроверяем все узлы. Блокировки меняются за часы, а не секунды.</summary>
    private static readonly TimeSpan HealthEvery = TimeSpan.FromMinutes(10);

    /// <summary>Пауза после запуска: дать окну подняться, а подписке — обновиться.</summary>
    private static readonly TimeSpan HealthFirstAfter = TimeSpan.FromSeconds(10);

    private DispatcherTimer? _healthTimer;
    private DispatcherTimer? _healthSoon;
    private DateTimeOffset? _lastProbeAt;

    /// <summary>
    /// Протоколы, не прошедшие ни на одном узле, — строкой для человека. Пусто,
    /// если таких нет или проверки ещё не было.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeadFamilies))]
    private string? _deadFamiliesText;

    public bool HasDeadFamilies => DeadFamiliesText is not null;

    /// <summary>Состояние выбранного узла, если он не проходит. Пусто — сказать нечего.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedHealth))]
    private string? _selectedHealthText;

    public bool HasSelectedHealth => SelectedHealthText is not null;

    private void StartHealthWatch()
    {
        _healthTimer = new DispatcherTimer { Interval = HealthEvery };
        _healthTimer.Tick += async (_, _) => await ProbeNodesAsync(background: true).ConfigureAwait(true);
        _healthTimer.Start();

        ProbeSoon(HealthFirstAfter);
    }

    /// <summary>
    /// Проверить в ближайшее время. Повторные вызовы сливаются в одну проверку:
    /// обновление нескольких подписок подряд не должно запускать их несколько.
    /// </summary>
    private void ProbeSoon(TimeSpan delay)
    {
        _healthSoon?.Stop();
        _healthSoon = new DispatcherTimer { Interval = delay };
        _healthSoon.Tick += async (_, _) =>
        {
            _healthSoon?.Stop();
            await ProbeNodesAsync(background: true).ConfigureAwait(true);
        };
        _healthSoon.Start();
    }

    /// <summary>
    /// Вердикт сторожа проходимости о выбранном узле. Ставится только после
    /// подтверждения — двух неудач подряд, как и предупреждение человеку: одна
    /// неудача бывает просто потерянным запросом.
    /// </summary>
    private void NoteActiveNodeVerdict(bool flowing)
    {
        if (SelectedServer is not { } item) return;

        item.Reachable = flowing;
        if (!flowing) item.Ping = null;
        item.MarkMeasured();

        UpdateHealthSummary();
    }

    /// <summary>Пересобрать сводку: что не проходит нигде и как дела у выбранного.</summary>
    private void UpdateHealthSummary()
    {
        var dead = Servers
            .Where(s => s.Reachable is not null)
            .GroupBy(s => s.Family)
            .Where(g => g.All(s => s.Reachable == false))
            .Select(g => $"{g.Key} (узлов: {g.Count()})")
            .OrderBy(s => s, StringComparer.CurrentCulture)
            .ToArray();

        JournalDeadFamilies(dead);

        var checkedAt = _lastProbeAt is { } at ? $" Проверено в {at:HH:mm}." : "";

        DeadFamiliesText = dead.Length switch
        {
            0 => null,
            1 => $"Не проходит ни на одном узле: {dead[0]}.{checkedAt}",
            _ => $"Не проходят ни на одном узле: {string.Join(", ", dead)}.{checkedAt}",
        };

        SelectedHealthText = SelectedServer is { Reachable: false } selected
            ? $"Узел не проходит: {selected.Family} через него сейчас не работает. Выберите другой."
            : null;
    }

    /// <summary>Набор мёртвых протоколов в прошлый раз: в журнал — смену, а не каждую проверку.</summary>
    private string _lastDeadKey = "";

    private void JournalDeadFamilies(string[] dead)
    {
        var key = string.Join("|", dead);
        if (key == _lastDeadKey) return;

        _lastDeadKey = key;
        WorkJournal.Add("Протоколы",
            dead.Length == 0 ? "все протоколы проходят хотя бы на одном узле" : $"не проходят нигде: {string.Join(", ", dead)}",
            dead.Length == 0 ? JournalOutcome.Ok : JournalOutcome.Failed);
    }
}
