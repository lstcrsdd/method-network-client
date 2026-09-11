// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Method.App.Controls;
using Method.App.Services;

namespace Method.App.Views;

/// <summary>
/// Заставка запуска.
///
/// <para>Правило у неё одно и оно важнее вида: заставка не задерживает работу.
/// Она держится, пока идут настоящие проверки, и уходит, как только они
/// кончились и последняя строка допечатана. Пропустить можно в любой момент —
/// щелчком или клавишей.</para>
///
/// <para>Нижний порог всё-таки есть. Проверки часто заканчиваются за сотню
/// миллисекунд, и без порога заставка мелькала бы вспышкой — а мелькание
/// раздражает сильнее, чем полсекунды ожидания. Верхний порог тоже есть: если
/// привилегированная часть не отвечает, ждать её человеку незачем, окно
/// откроется и без ответа, а строка дополнится позже.</para>
/// </summary>
public partial class BootScreenPoster : UserControl
{
    private static readonly TimeSpan MinimumShow = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan MaximumShow = TimeSpan.FromSeconds(6);

    private readonly DateTime _opened = DateTime.UtcNow;
    private readonly DispatcherTimer _leash;

    private Teletype _tape = null!;
    private bool _finished;
    private bool _gone;

    /// <summary>Заставке пора уходить.</summary>
    public event Action? Dismissed;

    public BootScreenPoster()
    {
        AvaloniaXamlLoader.Load(this);

        _tape = this.FindControl<Teletype>("Tape")!;
        this.FindControl<TextBlock>("StampText")!.Text = Stamp();

        // Строки, накопленные до того, как заставку успели показать: модель
        // начинает проверки в своём конструкторе, то есть раньше окна.
        foreach (var line in WorkJournal.Lines) _tape.Enqueue(line);

        WorkJournal.Added += OnAdded;
        WorkJournal.Finished += OnFinished;
        _tape.Drained += TryLeave;

        _leash = new DispatcherTimer { Interval = MaximumShow };
        _leash.Tick += (_, _) => Skip();
        _leash.Start();

        DetachedFromVisualTree += (_, _) => Unsubscribe();
        AttachedToVisualTree += (_, _) => Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Skip();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Skip();
    }

    private void OnAdded(JournalLine line) =>
        Dispatcher.UIThread.Post(() => _tape.Enqueue(line));

    private void OnFinished() =>
        Dispatcher.UIThread.Post(() =>
        {
            _finished = true;
            TryLeave();
        });

    /// <summary>
    /// Уйти, когда и проверки кончились, и лента допечатана. Оба условия
    /// обязательны: проверки часто заканчиваются раньше, чем строка о них
    /// доходит до экрана, и уход по одному их окончанию обрывал заставку на
    /// первой же строке.
    /// </summary>
    private void TryLeave()
    {
        if (!_finished || _gone || !_tape.IsIdle) return;

        var left = MinimumShow - (DateTime.UtcNow - _opened);

        if (left > TimeSpan.Zero)
        {
            DispatcherTimer.RunOnce(Leave, left);
            return;
        }

        Leave();
    }

    /// <summary>Человек не хочет ждать: допечатать разом и уйти.</summary>
    private void Skip()
    {
        if (_gone) return;

        _tape.Flush();
        Leave();
    }

    private void Leave()
    {
        if (_gone) return;

        _gone = true;
        Unsubscribe();
        Dismissed?.Invoke();
    }

    private void Unsubscribe()
    {
        _leash.Stop();
        WorkJournal.Added -= OnAdded;
        WorkJournal.Finished -= OnFinished;
        _tape.Drained -= TryLeave;
    }

    /// <summary>
    /// Шильдик изделия: версия клиента и версия системы. Оба числа настоящие —
    /// заставка про то, что под капотом, и начинать её с выдумки нельзя.
    /// </summary>
    private static string Stamp()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        var build = Environment.OSVersion.Version.Build;
        var windows = build >= 22000 ? "WINDOWS 11" : "WINDOWS 10";

        return $"ИЗДЕЛИЕ {version?.ToString(3) ?? "—"} · {windows} СБ {build} · {(Environment.Is64BitProcess ? "X64" : "X86")}";
    }
}
