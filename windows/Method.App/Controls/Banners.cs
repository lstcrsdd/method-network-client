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
/// Веер знамён за знаком — приём с праздничных плакатов: древки сходятся под
/// гербом, полотнища расходятся вверх и в стороны.
///
/// <para><b>Почему отдельный слой, а не часть знака.</b> Знак живёт в своём
/// квадрате и весь занят: обод, венок, картуш. Знамёна обязаны выходить за
/// него — иначе это не веер, а бахрома из-под краёв. Поэтому у них своя
/// система координат и своё место в разметке, за кнопкой.</para>
///
/// <para><b>Один ряд, четыре знамени на сторону.</b> Пробовали два ряда и
/// девять знамён — получилась каша: чем больше полотнищ, тем меньше каждое
/// читается флагом. Четырёх хватает, чтобы между ними не оставалось прогалин,
/// а веер оставался разборчивым.</para>
///
/// <para><b>Почему без движения.</b> Двигаются лучи, дрожит и вращается сам
/// знак — третий источник движения на том же пятачке превратил бы фон в
/// суету.</para>
/// </summary>
public sealed class Banners : Control
{
    /// <summary>Цвет полотнищ. Оттенки берутся от него же.</summary>
    public static readonly StyledProperty<IBrush?> ClothBrushProperty =
        AvaloniaProperty.Register<Banners, IBrush?>(nameof(ClothBrush));

    /// <summary>Древки и навершия.</summary>
    public static readonly StyledProperty<IBrush?> PoleBrushProperty =
        AvaloniaProperty.Register<Banners, IBrush?>(nameof(PoleBrush));

    public IBrush? ClothBrush { get => GetValue(ClothBrushProperty); set => SetValue(ClothBrushProperty, value); }
    public IBrush? PoleBrush { get => GetValue(PoleBrushProperty); set => SetValue(PoleBrushProperty, value); }

    static Banners() => AffectsRender<Banners>(ClothBrushProperty, PoleBrushProperty);

    // ── Разметка ──
    // Система 400 на 300; знак стоит в середине, знамёна расходятся из-под него.

    private const double Wide = 400;
    private const double High = 300;
    private const double RootX = 200;   // откуда растут древки
    private const double RootY = 262;

    /// <summary>
    /// Наклон древка от вертикали и его длина. Дальние от центра короче: на
    /// плакате веер вписан в дугу, а не в прямоугольник.
    /// </summary>
    private static readonly (double Tilt, double Length)[] Poles =
    [
        (64, 154),
        (48, 178),
        (32, 196),
        (16, 206),
    ];

    private static readonly (Geometry Shape, int Tone)[] Cloths = BuildCloths();
    private static readonly Geometry Shafts = BuildShafts();
    private static readonly Geometry Spears = BuildSpears();

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 2 || Bounds.Height <= 2) return;
        if (ClothBrush is not ISolidColorBrush cloth) return;

        var scale = Math.Min(Bounds.Width / Wide, Bounds.Height / High);
        var dx = (Bounds.Width - Wide * scale) / 2;
        var dy = (Bounds.Height - High * scale) / 2;

        using var place = context.PushTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy));

        var tones = new[] { Shade(cloth.Color, 0.50), Shade(cloth.Color, 0.70) };

        // Порядок важен: дальние знамёна ложатся первыми, ближние к центру
        // перекрывают их — как в стопке, которую держат в одной руке.
        foreach (var (shape, tone) in Cloths) context.DrawGeometry(tones[tone], null, shape);

        // Древки и навершия идут поверх ткани. Цвет у них близок к цвету
        // полотнищ, поэтому они не читаются палками — только намечают, на чём
        // знамёна держатся.
        context.DrawGeometry(PoleBrush, null, Shafts);
        context.DrawGeometry(PoleBrush, null, Spears);
    }

    /// <summary>Тот же тон, только глуше: знамёна стоят позади знака.</summary>
    private static IBrush Shade(Color color, double factor) =>
        new SolidColorBrush(Color.FromRgb(
            (byte)(color.R * factor),
            (byte)(color.G * factor),
            (byte)(color.B * factor)));

    /// <summary>Каждое полотнище — своя фигура: они рисуются по очереди и разным тоном.</summary>
    private static (Geometry, int)[] BuildCloths()
    {
        var list = new List<(Geometry, int)>();

        // От дальних к ближним и попарно слева-справа, чтобы веер складывался
        // симметрично. Тон чередуется — иначе соседние полотнища, налегая друг
        // на друга, сливаются в общее пятно.
        for (var i = 0; i < Poles.Length; i++)
        {
            var (tilt, length) = Poles[i];

            foreach (var side in new[] { -1, 1 })
                list.Add((Build(Cloth(side * tilt, length, side)), i % 2));
        }

        return list.ToArray();
    }

    /// <summary>
    /// Полотнище висит на верхней половине древка и уходит наружу.
    ///
    /// <para>Ширина взята заведомо больше промежутка между соседними древками:
    /// полотнища обязаны налегать друг на друга. Стоит сделать их у́же — между
    /// знамёнами открывается фон, и веер смотрится дырявым.</para>
    /// </summary>
    private static List<Point> Cloth(double tilt, double length, int side)
    {
        var head = Along(tilt, length * 0.95);      // верх у древка, под самым навершием
        var foot = Along(tilt, length * 0.50);      // низ крепления

        // Ткань уходит вбок и свисает вниз — по силе тяжести, а не по
        // перпендикуляру к древку. С перпендикуляром наклонное древко тянуло
        // полотнище вбок и вверх, и вместо флага выходил косой клин.
        var reach = length * 0.42;
        var drop = length * 0.20;

        var pts = new List<Point> { head, foot };

        // Свободный край идёт вдоль древка на постоянном отлёте: полотнище
        // одной ширины сверху донизу — это и есть флаг. Стоит пустить отлёт
        // нарастающим от нижнего угла, и полотнище сходится на древке в остриё —
        // выходит не флаг, а топор.
        const int steps = 18;

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;

            var spine = new Point(
                foot.X + (head.X - foot.X) * t,
                foot.Y + (head.Y - foot.Y) * t);

            // Свободный край провисает: у нижнего угла сильнее, у верхнего меньше.
            var sag = drop * (1 - 0.45 * t);

            pts.Add(new Point(
                spine.X + side * reach,
                spine.Y + sag + 5.5 * Math.Sin(Math.PI * t * 2.2)));
        }

        return pts;
    }

    private static Geometry BuildShafts()
    {
        var parts = new List<IReadOnlyList<Point>>();

        foreach (var (tilt, length) in Poles)
            foreach (var side in new[] { -1, 1 })
                parts.Add(Shaft(side * tilt, length));

        return Build(parts);
    }

    private static Geometry BuildSpears()
    {
        var parts = new List<IReadOnlyList<Point>>();

        foreach (var (tilt, length) in Poles)
            foreach (var side in new[] { -1, 1 })
                parts.Add(Spear(side * tilt, length));

        return Build(parts);
    }

    /// <summary>Древко — узкая полоса от основания к наконечнику.</summary>
    private static List<Point> Shaft(double tilt, double length)
    {
        var tip = Along(tilt, length);
        var n = Normal(tilt);

        return
        [
            new Point(RootX - n.X * 2.6, RootY - n.Y * 2.6),
            new Point(tip.X - n.X * 1.8, tip.Y - n.Y * 1.8),
            new Point(tip.X + n.X * 1.8, tip.Y + n.Y * 1.8),
            new Point(RootX + n.X * 2.6, RootY + n.Y * 2.6),
        ];
    }

    /// <summary>Навершие: копьецо, без него древко — просто палка.</summary>
    private static List<Point> Spear(double tilt, double length)
    {
        var neck = Along(tilt, length);
        var belly = Along(tilt, length + 6);
        var tip = Along(tilt, length + 17);
        var n = Normal(tilt);

        return
        [
            neck,
            new Point(belly.X + n.X * 4.8, belly.Y + n.Y * 4.8),
            tip,
            new Point(belly.X - n.X * 4.8, belly.Y - n.Y * 4.8),
        ];
    }

    /// <summary>Точка на древке: наклон отсчитывается от вертикали.</summary>
    private static Point Along(double tilt, double distance)
    {
        var rad = tilt * Math.PI / 180;
        return new Point(RootX + distance * Math.Sin(rad), RootY - distance * Math.Cos(rad));
    }

    /// <summary>Единичная нормаль к древку — ею задаётся толщина.</summary>
    private static Point Normal(double tilt)
    {
        var rad = tilt * Math.PI / 180;
        return new Point(Math.Cos(rad), Math.Sin(rad));
    }

    private static Geometry Build(List<Point> contour) => Build([contour]);

    private static Geometry Build(List<IReadOnlyList<Point>> contours)
    {
        var geometry = new StreamGeometry();
        using var draw = geometry.Open();

        draw.SetFillRule(FillRule.NonZero);

        foreach (var contour in contours)
        {
            draw.BeginFigure(contour[0], isFilled: true);
            for (var i = 1; i < contour.Count; i++) draw.LineTo(contour[i]);
            draw.EndFigure(true);
        }

        return geometry;
    }
}
