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
/// Точка состояния: тот же неон, что у кольца вокруг кнопки, только свёрнутый
/// в точку.
///
/// <para>Рецепт общий и лежит в <see cref="Neon"/>: слои одного цвета от
/// широкого и почти прозрачного снаружи к плотному внутри, а поверх — выбеленная
/// сердцевина. Разница с кольцом только в фигуре: там обводка окружности, здесь
/// заливка круга.</para>
///
/// <para>Погашенная точка остаётся серой и видимой: она сообщает состояние и
/// тогда, когда не горит. Между серым и неоном идёт не переключение, а
/// перетекание — по тому же числу, которым точка дышит, пока туннель
/// поднимается.</para>
/// </summary>
public sealed class StatusDot : Control
{
    /// <summary>Поперечник ядра в точках.</summary>
    private const double Core = 8;

    /// <summary>Сколько слоёв набирают ореол. Больше пяти глаз уже не различает.</summary>
    private const int Layers = 5;

    /// <summary>Во сколько раз ореол шире ядра.</summary>
    private const double Spread = 3.2;

    /// <summary>Сердцевина относительно ядра — та же доля, что у кольца.</summary>
    private const double Heart = 0.45;

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<StatusDot, IBrush?>(nameof(Fill));

    /// <summary>Цвет свечения. Им же горит и само ядро.</summary>
    public static readonly StyledProperty<IBrush?> GlowBrushProperty =
        AvaloniaProperty.Register<StatusDot, IBrush?>(nameof(GlowBrush));

    /// <summary>Насколько точка разгорелась: от нуля до единицы.</summary>
    public static readonly StyledProperty<double> GlowOpacityProperty =
        AvaloniaProperty.Register<StatusDot, double>(nameof(GlowOpacity));

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
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

    static StatusDot()
    {
        AffectsRender<StatusDot>(FillProperty, GlowBrushProperty, GlowOpacityProperty);
    }

    public override void Render(DrawingContext context)
    {
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var lit = Math.Clamp(GlowOpacity, 0, 1);
        var radius = Core / 2;

        // Погашенное ядро уступает место горящему постепенно: иначе на переходе
        // серое и цветное складывались бы в мутный промежуточный тон.
        if (lit < 0.999 && Fill is { } dull)
        {
            using var _ = context.PushOpacity(1 - lit);
            context.DrawEllipse(dull, null, centre, radius, radius);
        }

        if (lit <= 0.001 || GlowBrush is not { } glow) return;

        // Снаружи внутрь: широкий и почти прозрачный круг даёт ореол, каждый
        // следующий уже и плотнее, последний — само ядро. Обратный порядок
        // положил бы мутное поверх яркого и погасил его.
        for (var i = Layers; i >= 1; i--)
        {
            var step = (double)i / Layers;
            var wide = radius * (1 + (Spread - 1) * step);

            using var _ = context.PushOpacity(Neon.LayerOpacity(i, step) * lit);
            context.DrawEllipse(glow, null, centre, wide, wide);
        }

        if (Neon.Filament(glow) is not { } heart) return;

        using var _heart = context.PushOpacity(lit);
        context.DrawEllipse(heart, null, centre, radius * Heart, radius * Heart);
    }
}
