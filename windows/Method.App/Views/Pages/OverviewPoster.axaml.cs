// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Method.App.Controls;
using Method.App.Services;

namespace Method.App.Views.Pages;

/// <summary>
/// Обзор в плакатном оформлении. Подгонку размеров знака под окно взяла на себя
/// коробка масштабирования в разметке; здесь остаётся лента журнала — она живёт
/// событиями, а не разметкой, — и признак просторной страницы.
/// </summary>
public partial class OverviewPoster : UserControl
{
    /// <summary>
    /// Сколько прошлых строк показать при открытии раздела. Печатать всю историю
    /// сеанса заново — значит заставить человека ждать конца печати ради того,
    /// что он уже видел; лента показывает хвост и продолжает с него.
    /// </summary>
    private const int Backlog = 12;

    /// <summary>
    /// С какой ширины страница считается просторной.
    ///
    /// <para>Ниже этого — обычное окно, и там размеры выверены: трогать их не
    /// нужно. Выше начинается развёрнутое на весь монитор, где та же сводка
    /// занимает шестую часть ширины и читается припиской сбоку. Разметка
    /// условий по размеру не знает, поэтому признак ставится отсюда классом, а
    /// сами размеры остаются в стилях — там, где им и место.</para>
    ///
    /// <para>Порог выбран с запасом от полутора тысяч: на 1180, с каким окно
    /// открывается, и на любом разумном увеличении мышью он не срабатывает.</para>
    /// </summary>
    private const double Spacious = 1500;

    private readonly Teletype _tape;

    public OverviewPoster()
    {
        AvaloniaXamlLoader.Load(this);

        _tape = this.FindControl<Teletype>("Tape")!;

        var page = this.FindControl<Grid>("Page")!;
        var glass = this.FindControl<TubeScreen>("CodeGlass")!;

        // Признак ставится по ширине самой страницы, а не окна: у окна есть ещё
        // боковая панель, и её ширина к этой раскладке отношения не имеет.
        SizeChanged += (_, e) =>
        {
            var spacious = e.NewSize.Width >= Spacious;

            page.Classes.Set("spacious", spacious);

            // Бегущий код прячем не прозрачностью, а из разметки целиком: у него
            // свой таймер, и невидимая лента крутила бы его впустую.
            glass.IsVisible = spacious;
        };

        var lines = WorkJournal.Lines;
        foreach (var line in lines.Skip(Math.Max(0, lines.Count - Backlog))) _tape.Enqueue(line);

        // Прошлое выводим сразу: печатать имеет смысл то, что происходит на
        // глазах, а не то, что случилось до открытия раздела.
        _tape.Flush();

        WorkJournal.Added += OnAdded;
        DetachedFromVisualTree += (_, _) => WorkJournal.Added -= OnAdded;
    }

    private void OnAdded(JournalLine line) =>
        Dispatcher.UIThread.Post(() => _tape.Enqueue(line));
}
