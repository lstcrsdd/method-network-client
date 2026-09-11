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
/// Знак, набранный неоновой трубкой. Рецепт общий с кольцом вокруг кнопки и
/// точкой состояния, он описан в <see cref="Neon"/>; здесь только контур
/// сложнее окружности.
///
/// <para>Слои были тремя <c>Path</c> друг поверх друга, и это было неверно: при
/// <c>Stretch="Uniform"</c> фигура вписывается в отведённое место <b>вместе с
/// пером</b>, поэтому слой с широким ореолом получал заметно меньший масштаб,
/// чем тонкая нить. Слои расходились — знак уезжал вбок, свечение ложилось
/// несимметрично. Здесь путь один, и преобразование для всех слоёв одно.</para>
///
/// <para>Белой нити внутри нет намеренно. Белая черта поверх цветной обводки
/// читается не разрядом в трубке, а буквой, обведённой по контуру, — ровно то,
/// на что смотришь и видишь «белую V и обводку». Сердцевина здесь того же
/// цвета, лишь уведённого к белому, и она узкая: свет должен угадываться
/// внутри трубки, а не лежать поверх неё.</para>
///
/// <para>Толщины заданы в точках экрана, а не в единицах пути: перо делится на
/// масштаб, иначе смена размера знака меняла бы и толщину трубки.</para>
/// </summary>
public sealed class NeonMark : Control
{
    /// <summary>Сколько слоёв набирают ореол. Больше пяти глаз уже не различает.</summary>
    private const int Layers = 5;

    /// <summary>Сердцевина относительно нити — та же доля, что у кольца.</summary>
    private const double Heart = 0.45;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<NeonMark, Geometry?>(nameof(Data));

    /// <summary>Цвет погашенного знака. Горящий светится своим цветом.</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<NeonMark, IBrush?>(nameof(Stroke));

    /// <summary>Цвет свечения.</summary>
    public static readonly StyledProperty<IBrush?> GlowBrushProperty =
        AvaloniaProperty.Register<NeonMark, IBrush?>(nameof(GlowBrush));

    /// <summary>Насколько знак разгорелся: от нуля до единицы.</summary>
    public static readonly StyledProperty<double> GlowOpacityProperty =
        AvaloniaProperty.Register<NeonMark, double>(nameof(GlowOpacity));

    /// <summary>Толщина нити в точках.</summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<NeonMark, double>(nameof(Thickness), 1.1);

    /// <summary>Во сколько раз ореол шире нити.</summary>
    public static readonly StyledProperty<double> GlowSpreadProperty =
        AvaloniaProperty.Register<NeonMark, double>(nameof(GlowSpread), 7);

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? GlowBrush
    {
        get => GetValue(GlowBrushProperty);
        set => SetValue(GlowBrushProperty, value);
    }

    public double GlowOpacity
    {
        get => GetValue(GlowOpacityProperty);
        set => SetValue(GlowOpacityProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public double GlowSpread
    {
        get => GetValue(GlowSpreadProperty);
        set => SetValue(GlowSpreadProperty, value);
    }

    static NeonMark()
    {
        AffectsRender<NeonMark>(
            DataProperty, StrokeProperty, GlowBrushProperty,
            GlowOpacityProperty, ThicknessProperty, GlowSpreadProperty);
    }

    public override void Render(DrawingContext context)
    {
        var data = Data;

        if (data is null) return;

        var box = data.Bounds;

        if (box.Width <= 0 || box.Height <= 0) return;

        var scale = Math.Min(Bounds.Width / box.Width, Bounds.Height / box.Height);

        if (scale <= 0) return;

        // Середина пути — в середину отведённого места. Считаем от границ самого
        // пути, а не от его начала координат: знак должен стоять по центру диска
        // независимо от того, в каких единицах он нарисован.
        var place =
            Matrix.CreateTranslation(-box.X - box.Width / 2, -box.Y - box.Height / 2) *
            Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation(Bounds.Width / 2, Bounds.Height / 2);

        using var _ = context.PushTransform(place);

        var lit = Math.Clamp(GlowOpacity, 0, 1);
        var thread = Thickness / scale;

        // Погашенный знак уступает место горящему постепенно: иначе на переходе
        // серая линия и цветная складывались бы в мутный промежуточный тон.
        if (lit < 0.999 && Stroke is { } dull)
            Draw(context, data, dull, thread, 1 - lit);

        if (lit <= 0.001 || GlowBrush is not { } glow) return;

        // Снаружи внутрь: широкая и почти прозрачная обводка даёт ореол, каждая
        // следующая уже и плотнее, последняя — сама нить.
        for (var i = Layers; i >= 1; i--)
        {
            var step = (double)i / Layers;
            var width = thread * (1 + (Math.Max(1, GlowSpread) - 1) * step);

            Draw(context, data, glow, width, Neon.LayerOpacity(i, step) * lit);
        }

        if (Neon.Filament(glow) is { } heart)
            Draw(context, data, heart, thread * Heart, lit);
    }

    private static void Draw(
        DrawingContext context, Geometry data, IBrush brush, double width, double opacity)
    {
        using var _ = context.PushOpacity(opacity);

        context.DrawGeometry(
            null,
            new Pen(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
            data);
    }
}
