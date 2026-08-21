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
/// График скорости: линия по последним замерам и мягкая заливка под ней.
///
/// <para>Своя отрисовка, а не готовый график: нужна одна линия без осей, подписей
/// и легенды, и тянуть ради неё библиотеку значило бы получить вместе с ней
/// оформление, которое придётся перебивать целиком.</para>
///
/// <para>Масштаб по вертикали плавающий — по наибольшему значению в окне. Иначе
/// на быстром канале мелкий трафик выглядел бы прямой линией у нуля, а смысл
/// графика именно в форме, а не в абсолютных числах: они рядом цифрами.</para>
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    static Sparkline() =>
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty);

    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (values is null || values.Count < 2) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        var max = values.Max();

        // Пол у масштаба нужен: без него на полном затишье шум в сотню байт
        // рисовался бы во всю высоту и выглядел бы бурей.
        if (max < 1024) max = 1024;

        var step = width / (values.Count - 1);
        var points = new Point[values.Count];

        for (var i = 0; i < values.Count; i++)
        {
            var y = height - values[i] / max * (height - 2) - 1;
            points[i] = new Point(i * step, y);
        }

        var line = new StreamGeometry();
        using (var draw = line.Open())
        {
            draw.BeginFigure(points[0], isFilled: false);
            for (var i = 1; i < points.Length; i++) draw.LineTo(points[i]);
            draw.EndFigure(false);
        }

        var area = new StreamGeometry();
        using (var draw = area.Open())
        {
            draw.BeginFigure(new Point(0, height), isFilled: true);
            foreach (var point in points) draw.LineTo(point);
            draw.LineTo(new Point(width, height));
            draw.EndFigure(true);
        }

        var stroke = Stroke ?? Brushes.White;

        if (stroke is ISolidColorBrush solid)
        {
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(64, solid.Color.R, solid.Color.G, solid.Color.B), 0),
                    new GradientStop(Color.FromArgb(0, solid.Color.R, solid.Color.G, solid.Color.B), 1),
                },
            };

            context.DrawGeometry(fill, null, area);
        }

        context.DrawGeometry(null, new Pen(stroke, 1.5, lineJoin: PenLineJoin.Round), line);
    }
}
