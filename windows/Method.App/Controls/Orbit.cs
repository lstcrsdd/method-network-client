// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Method.App.Controls;

/// <summary>
/// Неоновое кольцо вокруг кнопки подключения.
///
/// <para>Свечение набирается обводками: несколько окружностей одного радиуса,
/// от широкой и почти прозрачной снаружи к тонкой и яркой внутри. Так устроена
/// и настоящая газоразрядная трубка — яркая нить и ореол вокруг неё, — и так
/// это делается на экране, где размытия под рукой нет: тень у Avalonia есть
/// только у прямоугольных рамок, а нам нужен круг.</para>
///
/// <para>Ничего не движется и не мигает. Движение здесь пробовали — дышащее
/// гало и бегущая по кругу дуга — и убрали: неон не крутится, он просто горит.
/// Заодно ушёл таймер, а с ним и его доля процессора.</para>
/// </summary>
public sealed class Orbit : Control
{
    /// <summary>Сколько обводок набирают ореол. Больше пяти глаз уже не различает.</summary>
    private const int Layers = 5;

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<Orbit, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> GlowBrushProperty =
        AvaloniaProperty.Register<Orbit, IBrush?>(nameof(GlowBrush));

    public static readonly StyledProperty<double> TrackThicknessProperty =
        AvaloniaProperty.Register<Orbit, double>(nameof(TrackThickness), 1.5);

    /// <summary>Толщина горящей нити. Ореол вокруг неё строится от этого числа.</summary>
    public static readonly StyledProperty<double> GlowThicknessProperty =
        AvaloniaProperty.Register<Orbit, double>(nameof(GlowThickness), 2.5);

    /// <summary>Насколько широк ореол — во сколько раз шире нити.</summary>
    public static readonly StyledProperty<double> GlowSpreadProperty =
        AvaloniaProperty.Register<Orbit, double>(nameof(GlowSpread), 7);

    /// <summary>Горит ли кольцо. Погашенное оставляет только тонкий след.</summary>
    public static readonly StyledProperty<bool> IsLitProperty =
        AvaloniaProperty.Register<Orbit, bool>(nameof(IsLit));

    /// <summary>
    /// Насколько кольцо разгорелось: от нуля до единицы.
    ///
    /// <para>Отдельное число, а не просто прозрачность всего контрола, потому
    /// что след при зажигании гаснуть не должен — он остаётся на месте, пока
    /// поверх него разгорается нить.</para>
    ///
    /// <para>Плавность даёт переход, объявленный в наборе стилей. Это обычное
    /// вещественное свойство, и аниматор для таких у Avalonia есть из коробки —
    /// в отличие от преобразований, на которых клиент уже падал при попытке
    /// раскрутить это же кольцо.</para>
    /// </summary>
    public static readonly StyledProperty<double> GlowOpacityProperty =
        AvaloniaProperty.Register<Orbit, double>(nameof(GlowOpacity));

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? GlowBrush
    {
        get => GetValue(GlowBrushProperty);
        set => SetValue(GlowBrushProperty, value);
    }

    public double TrackThickness
    {
        get => GetValue(TrackThicknessProperty);
        set => SetValue(TrackThicknessProperty, value);
    }

    public double GlowThickness
    {
        get => GetValue(GlowThicknessProperty);
        set => SetValue(GlowThicknessProperty, value);
    }

    public double GlowSpread
    {
        get => GetValue(GlowSpreadProperty);
        set => SetValue(GlowSpreadProperty, value);
    }

    public bool IsLit
    {
        get => GetValue(IsLitProperty);
        set => SetValue(IsLitProperty, value);
    }

    public double GlowOpacity
    {
        get => GetValue(GlowOpacityProperty);
        set => SetValue(GlowOpacityProperty, value);
    }

    static Orbit()
    {
        AffectsRender<Orbit>(
            TrackBrushProperty, GlowBrushProperty, TrackThicknessProperty,
            GlowThicknessProperty, GlowSpreadProperty, GlowOpacityProperty);

        // Признак «горит» переводится в число, а число уже разгорается плавно
        // переходом из набора стилей. Сам признак при этом остаётся булевым —
        // разметке незачем знать про доли зажигания.
        IsLitProperty.Changed.AddClassHandler<Orbit>(
            (orbit, e) => orbit.GlowOpacity = e.NewValue is true ? 1 : 0);
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 4) return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var widest = Math.Max(TrackThickness, GlowThickness * Math.Max(1, GlowSpread));
        var radius = (side - widest) / 2;

        if (radius <= 1) return;

        if (TrackBrush is { } track)
            context.DrawEllipse(null, new Pen(track, TrackThickness), centre, radius, radius);

        var lit = Math.Clamp(GlowOpacity, 0, 1);
        if (lit <= 0.001 || GlowBrush is not { } glow) return;

        // Снаружи внутрь: широкая и почти прозрачная обводка даёт ореол, каждая
        // следующая уже и плотнее, последняя — сама нить. Обратный порядок
        // положил бы мутный слой поверх яркого и погасил его.
        for (var i = Layers; i >= 1; i--)
        {
            var step = (double)i / Layers;

            var thickness = GlowThickness * (1 + (Math.Max(1, GlowSpread) - 1) * step);

            // Прозрачность падает быстрее толщины: ореол должен угадываться, а не
            // выглядеть вторым кольцом.
            using var _ = context.PushOpacity(Neon.LayerOpacity(i, step) * lit);
            context.DrawEllipse(null, new Pen(glow, thickness), centre, radius, radius);
        }

        // Сердцевина: нить светлее собственного цвета. У газоразрядной трубки
        // середина разряда всегда выбелена, и без этого кольцо читается просто
        // размытой чертой, а не горящей линией.
        if (Neon.Filament(glow) is not { } core) return;

        using var _core = context.PushOpacity(lit);
        context.DrawEllipse(null, new Pen(core, Math.Max(1, GlowThickness * 0.45)), centre, radius, radius);
    }
}
