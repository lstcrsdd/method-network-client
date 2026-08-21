// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Method.App.Controls;

/// <summary>
/// Плавная прокрутка колесом мыши. Включается в разметке:
/// <c>c:SmoothScroll.Enabled="True"</c>.
///
/// <para><b>Зачем.</b> У движка интерфейса интерполяции колеса нет вовсе. Штатный
/// обработчик на каждый щелчок прибавляет к смещению фиксированный шаг и сразу
/// перерисовывает — промежуточных кадров не существует, поэтому прокрутка выглядит
/// рывками. Инерция в движке есть, но только для касаний, к колесу она отношения
/// не имеет.</para>
///
/// <para><b>Как сделано.</b> Щелчок перехватывается в фазе туннелирования, то есть
/// до штатного обработчика, копится в своей цели, а фактическое смещение
/// подтягивается к ней покадрово.</para>
///
/// <para><b>Почему своя цель, а не переход на смещении.</b> Переход считал бы
/// следующий щелчок от текущего, ещё летящего значения — при быстрой прокрутке
/// лента недокручивает. Здесь щелчки складываются полностью.</para>
///
/// <para><b>Почему по кадрам отрисовки, а не по таймеру на шестьдесят герц.</b>
/// Нет биений с частотой монитора, а шаг считается по фактическому времени кадра —
/// на 144 герцах и на 60 скорость одинаковая.</para>
/// </summary>
public static class SmoothScroll
{
    /// <summary>Сколько пикселей даёт один щелчок колеса. Шестьдесят — примерно системные «три строки».</summary>
    private const double WheelStep = 60;

    /// <summary>Постоянная времени сглаживания, с. Меньше — резче, больше — «кисельнее».</summary>
    private const double Tau = 0.08;

    /// <summary>Порог, ниже которого доводить нечего: полпикселя на экране не видно.</summary>
    private const double Epsilon = 0.5;

    /// <summary>Ограничение шага времени: после подвисания не прыгать через полэкрана.</summary>
    private const double MaxFrameSeconds = 0.1;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Enabled", typeof(SmoothScroll));

    public static void SetEnabled(ScrollViewer host, bool value) => host.SetValue(EnabledProperty, value);

    public static bool GetEnabled(ScrollViewer host) => host.GetValue(EnabledProperty);

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, State?>("State", typeof(SmoothScroll));

    static SmoothScroll() =>
        EnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnEnabledChanged);

    private static void OnEnabledChanged(ScrollViewer scroll, AvaloniaPropertyChangedEventArgs e)
    {
        var enabled = e.NewValue is true;

        // Отписываемся всегда: повторная подписка на тот же обработчик дала бы
        // двойной шаг.
        scroll.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        scroll.PropertyChanged -= OnScrollPropertyChanged;
        scroll.DetachedFromVisualTree -= OnDetached;

        if (!enabled)
        {
            Stop(scroll);
            return;
        }

        // Туннелирование обязательно: штатный обработчик живёт в потомке и
        // срабатывает на всплытии, то есть уже после нас. Иначе рывок случится всё
        // равно, а мы бы только добавили сверху вторую прокрутку.
        scroll.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        scroll.PropertyChanged += OnScrollPropertyChanged;
        scroll.DetachedFromVisualTree += OnDetached;
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer scroll) return;

        // Касания и перо отдаём движку: там своя инерция, и две анимации подрались бы.
        if (e.Pointer.Type != PointerType.Mouse) return;

        var delta = e.Delta.Y;
        if (delta == 0) return;

        // Вложенные списки: щелчок принадлежит ближайшему прокручиваемому предку
        // курсора. Внешний прокрутчик туннелирует раньше вложенного, поэтому без
        // этой проверки он забирал бы колесо себе, даже когда курсор стоит над
        // внутренним списком. А когда внутренний упёрся в край, ближайшим окажется
        // внешний — так и получается сцепление.
        if (!ReferenceEquals(ResolveTarget(e.Source as Visual, delta), scroll)) return;

        var state = GetOrCreateState(scroll);
        var max = MaxOffset(scroll);

        // Считаем от цели, а не от текущего положения — иначе быстрая прокрутка
        // теряет расстояние: каждый следующий щелчок стартовал бы с полпути.
        var from = state.Animating ? state.Target : scroll.Offset.Y;
        var target = from - delta * WheelStep;

        // Цель прибиваем к целому пикселю: остановиться на дробном значении значит
        // оставить содержимое размытым уже после того, как прокрутка кончилась.
        state.Target = Snap(scroll, target < 0 ? 0 : target > max ? max : target);
        e.Handled = true;

        if (state.Animating) return;

        state.Animating = true;
        state.HasFrameTime = false;
        state.Position = scroll.Offset.Y;

        // Новое поколение: уже поставленный кадровый вызов от прошлой анимации снять
        // нельзя, поэтому он должен сам понять, что устарел. Без этого счётчика
        // отменённая анимация оставляла висеть вызов, следующий щелчок заводил второй
        // цикл к той же цели, и лента ехала вдвое быстрее — рывком, ровно тем, ради
        // устранения которого всё это и написано.
        state.Generation++;
        RequestFrame(scroll, state, state.Generation);
    }

    private static void RequestFrame(ScrollViewer scroll, State state, int generation)
    {
        var top = TopLevel.GetTopLevel(scroll);
        if (top is null)
        {
            state.Animating = false;
            return;
        }

        top.RequestAnimationFrame(now => Step(scroll, state, generation, now));
    }

    private static void Step(ScrollViewer scroll, State state, int generation, TimeSpan now)
    {
        if (!state.Animating || generation != state.Generation) return;

        if (TopLevel.GetTopLevel(scroll) is null)
        {
            state.Animating = false;
            return;
        }

        var seconds = state.HasFrameTime ? (now - state.LastFrame).TotalSeconds : 1.0 / 60;
        state.LastFrame = now;
        state.HasFrameTime = true;

        if (seconds <= 0) seconds = 1.0 / 60;
        else if (seconds > MaxFrameSeconds) seconds = MaxFrameSeconds;

        // Экспоненциальное сглаживание по фактическому времени кадра: на любой
        // частоте обновления путь до цели занимает одно и то же время.
        //
        // Считаем от собственного положения, а не от того, что записано в списке:
        // записываем мы прибитое к пикселю значение, и считать следующий шаг от
        // него значило бы каждый кадр терять остаток. На малых скоростях остаток
        // меньше половины пикселя, движение останавливалось бы вовсе.
        var current = state.Position;
        var next = current + (state.Target - current) * (1 - Math.Exp(-seconds / Tau));

        if (Math.Abs(state.Target - next) < Epsilon)
        {
            next = state.Target;
            state.Animating = false;
        }

        state.Position = next;

        // На экран уходит целое число пикселей. Дробное смещение заставляет движок
        // каждый кадр рисовать текст и волосяные рамки с новой долей пикселя — глаз
        // читает это как размытие и двоение, хотя двигается всё ровно.
        var snapped = Snap(scroll, next);

        // Лишняя запись того же значения — лишняя перерисовка на ровном месте.
        if (Math.Abs(snapped - state.Written) >= 0.01 || !state.HasWritten)
        {
            state.Written = snapped;
            state.HasWritten = true;
            scroll.Offset = new Vector(scroll.Offset.X, snapped);
        }

        if (state.Animating) RequestFrame(scroll, state, generation);
    }

    /// <summary>
    /// Смещение изменил кто-то другой — ползунок, клавиатура, показ элемента,
    /// смена размера. Наша цель после этого устарела: продолжив, мы дёрнули бы
    /// ленту обратно.
    /// </summary>
    private static void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty || sender is not ScrollViewer scroll) return;

        var state = scroll.GetValue(StateProperty);
        if (state is null || !state.Animating || e.NewValue is not Vector offset) return;

        if (Math.Abs(offset.Y - state.Written) > Epsilon)
        {
            state.Animating = false;
            state.Target = offset.Y;
            state.Position = offset.Y;
        }
    }

    /// <summary>
    /// Прибить к целому пикселю устройства. Именно устройства, а не разметки: при
    /// масштабе 125 или 150 процентов целое значение в единицах разметки попадает
    /// на середину настоящего пикселя, и размытие остаётся.
    /// </summary>
    private static double Snap(ScrollViewer scroll, double value)
    {
        var scaling = TopLevel.GetTopLevel(scroll)?.RenderScaling ?? 1.0;
        if (scaling <= 0) scaling = 1.0;

        return Math.Round(value * scaling, MidpointRounding.AwayFromZero) / scaling;
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is ScrollViewer scroll) Stop(scroll);
    }

    private static void Stop(ScrollViewer scroll)
    {
        if (scroll.GetValue(StateProperty) is not { } state) return;

        state.Animating = false;
        state.Target = scroll.Offset.Y;
        state.Position = scroll.Offset.Y;
    }

    /// <summary>Ближайший к курсору прокручиваемый в нужную сторону список.</summary>
    private static ScrollViewer? ResolveTarget(Visual? source, double delta)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
            if (visual is ScrollViewer scroll && CanScroll(scroll, delta))
                return scroll;

        return null;
    }

    private static bool CanScroll(ScrollViewer scroll, double delta)
    {
        var max = MaxOffset(scroll);
        if (max <= 0) return false;

        // Положение с учётом незавершённой анимации: у самого края иначе можно
        // отдать щелчок не тому списку.
        var state = scroll.GetValue(StateProperty);
        var position = state is { Animating: true } ? state.Target : scroll.Offset.Y;

        return delta < 0 ? position < max - Epsilon : position > Epsilon;
    }

    private static double MaxOffset(ScrollViewer scroll)
    {
        var max = scroll.Extent.Height - scroll.Viewport.Height;
        return max > 0 ? max : 0;
    }

    private static State GetOrCreateState(ScrollViewer scroll)
    {
        if (scroll.GetValue(StateProperty) is { } existing) return existing;

        var state = new State
        {
            Target = scroll.Offset.Y,
            Position = scroll.Offset.Y,
            Written = scroll.Offset.Y,
        };
        scroll.SetValue(StateProperty, state);
        return state;
    }

    private sealed class State
    {
        /// <summary>Куда едем — накопленная сумма щелчков, прибитая к пикселю.</summary>
        public double Target;

        /// <summary>
        /// Где мы на самом деле, с дробной частью. На экран уходит округлённое
        /// значение, а движение считается по этому — иначе остаток теряется каждый
        /// кадр и на малой скорости прокрутка встаёт.
        /// </summary>
        public double Position;

        /// <summary>Что записали последним: по нему отличаем своё изменение от чужого.</summary>
        public double Written;

        public bool HasWritten;
        public bool Animating;
        public bool HasFrameTime;
        public TimeSpan LastFrame;

        /// <summary>Номер текущей анимации: кадровый вызов от прошлой обязан себя опознать и выйти.</summary>
        public int Generation;
    }
}
