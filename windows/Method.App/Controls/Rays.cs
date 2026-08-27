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
using Avalonia.Threading;

namespace Method.App.Controls;

/// <summary>
/// Расходящиеся лучи за знаком — плакатное солнце.
///
/// <para><b>Почему не круг.</b> Круглое гало вокруг круглой шестерни давало
/// обод вокруг обода: две одинаковые формы, вложенные друг в друга, спорят, а
/// не поддерживают. Лучи задают направление от центра наружу — то же свечение,
/// но работающее на знак, а не рядом с ним.</para>
///
/// <para><b>Почему так тускло.</b> Это фон. Предел непрозрачности здесь —
/// сотые доли, и поднимать его нельзя: как только лучи становятся различимы
/// сами по себе, взгляд уходит на них, а знак перестаёт быть главным. Поэтому
/// и затухание к краю: у центра, под самим знаком, свет плотнее, а к периферии
/// сходит на нет, не оставляя обрезанного круга.</para>
///
/// <para><b>Почему движение своё, а не анимацией стилей.</b> Ровно та же
/// причина, что у знака: покадровая анимация преобразований в Avalonia требует
/// своего аниматора и без него роняет окно на старте. Здесь один таймер на
/// двадцать кадров — для оборота за пять минут этого с запасом.</para>
/// </summary>
public sealed class Rays : Control
{
    /// <summary>Цвет свечения. Прозрачность задаётся здесь, а не в кисти.</summary>
    public static readonly StyledProperty<IBrush?> RayBrushProperty =
        AvaloniaProperty.Register<Rays, IBrush?>(nameof(RayBrush));

    /// <summary>Туннель поднят: свет прибавляет и дышит заметнее.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<Rays, bool>(nameof(IsActive));

    public IBrush? RayBrush { get => GetValue(RayBrushProperty); set => SetValue(RayBrushProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    static Rays() => AffectsRender<Rays>(RayBrushProperty, IsActiveProperty);

    // ── Разметка ──
    // Как и у знака, всё в системе 200 на 200 и потом масштабируется под место.

    private const double Side = 200;
    private const int Count = 24;           // столько лучей вместе с промежутками
    private const double Hub = 7;           // лучи расходятся от пятачка, а не от точки
    private const double Reach = 100;

    /// <summary>Покой: свет едва угадывается.</summary>
    private const double IdleBase = 0.045;
    private const double IdleSwing = 0.018;

    /// <summary>Работа: света больше, но всё ещё меньше, чем у самого знака.</summary>
    private const double LiveBase = 0.115;
    private const double LiveSwing = 0.045;

    private const double IdlePeriod = 5.5;  // секунды на вдох и выдох
    private const double LivePeriod = 3.4;
    private const double SpinSpeed = 1.2;   // градусов в секунду, оборот за пять минут
    private const double FrameSeconds = 0.05;

    private static readonly Geometry Fan = BuildFan();

    private DispatcherTimer? _timer;
    private double _phase;
    private double _angle;

    private RadialGradientBrush? _brush;
    private Color _brushColor;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _timer ??= CreateTimer();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(FrameSeconds) };

        timer.Tick += (_, _) =>
        {
            _phase += FrameSeconds;
            _angle = (_angle + SpinSpeed * FrameSeconds) % 360;
            InvalidateVisual();
        };

        return timer;
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 2) return;

        if (RayBrush is not ISolidColorBrush solid) return;

        var period = IsActive ? LivePeriod : IdlePeriod;
        var swing = 0.5 + 0.5 * Math.Sin(2 * Math.PI * _phase / period);

        var opacity = IsActive
            ? LiveBase + LiveSwing * swing
            : IdleBase + IdleSwing * swing;

        var scale = size / Side;
        var dx = (Bounds.Width - size) / 2;
        var dy = (Bounds.Height - size) / 2;

        using var opaque = context.PushOpacity(opacity);
        using var place = context.PushTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy));

        // Вращение вокруг середины знака, а не вокруг угла холста.
        using var spin = context.PushTransform(
            Matrix.CreateTranslation(-Side / 2, -Side / 2)
            * Matrix.CreateRotation(_angle * Math.PI / 180)
            * Matrix.CreateTranslation(Side / 2, Side / 2));

        context.DrawGeometry(BrushFor(solid.Color), null, Fan);
    }

    /// <summary>
    /// Кисть с затуханием к краю. Пересобираем только при смене цвета: она
    /// перерисовывается двадцать раз в секунду, и лепить её каждый кадр значило
    /// бы держать сборщик мусора занятым ради неизменной картинки.
    /// </summary>
    private RadialGradientBrush BrushFor(Color color)
    {
        if (_brush is not null && _brushColor == color) return _brush;

        _brushColor = color;
        _brush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(color, 0),
                new GradientStop(color, 0.45),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            ],
        };

        return _brush;
    }

    /// <summary>Веер клиньев: луч и промежуток равной ширины.</summary>
    private static Geometry BuildFan()
    {
        const double step = 360.0 / Count;
        const double half = step / 4;       // луч занимает половину своего сектора

        var geometry = new StreamGeometry();
        using var draw = geometry.Open();

        for (var i = 0; i < Count; i++)
        {
            // Отсчёт от верхней точки — по той же причине, что и у обода знака:
            // вертикальная ось знака должна попадать в луч, а не мимо.
            var a = 90 + i * step;

            draw.BeginFigure(Polar(Hub, a - half), isFilled: true);
            draw.LineTo(Polar(Reach, a - half));
            draw.LineTo(Polar(Reach, a + half));
            draw.LineTo(Polar(Hub, a + half));
            draw.EndFigure(true);
        }

        return geometry;
    }

    private static Point Polar(double r, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(Side / 2 + r * Math.Cos(rad), Side / 2 - r * Math.Sin(rad));
    }
}
