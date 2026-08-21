// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Method.App.Controls;

/// <summary>
/// Знак на кнопке подключения: шестерня, флаг, серп и молот, звезда в картуше.
///
/// <para><b>Почему рисуем кодом, а не строками путей.</b> Знак собран из
/// полутора сотен фигур — дуг, зубцов, колосьев. Выгруженный в разметку он был
/// бы килобайтами координат, в которых нельзя ни поправить толщину лезвия, ни
/// добавить зубец. Здесь он собирается из параметров: ширина обода, число
/// зубцов, изгиб серпа — это числа, которые видно и можно менять.</para>
///
/// <para><b>Почему шестерня движется.</b> Знак должен читаться органом
/// управления, а не наклейкой. Кольцо шестерни само по себе мишень — глаз
/// опознаёт замкнутый обод как нажимаемое. Движение договаривает остальное:
/// при попытке подключиться механизм дёргается, будто его заело, при рабочем
/// туннеле — ровно вращается. Состояние читается без единого слова, причём на
/// языке самого знака.</para>
/// </summary>
public sealed class Emblem : Control
{
    // ── Что рисуем ──

    /// <summary>Оправа: шестерня, картуш, колосья.</summary>
    public static readonly StyledProperty<IBrush?> FrameBrushProperty =
        AvaloniaProperty.Register<Emblem, IBrush?>(nameof(FrameBrush));

    /// <summary>Серп, молот и звезда — единственное яркое пятно.</summary>
    public static readonly StyledProperty<IBrush?> ToolBrushProperty =
        AvaloniaProperty.Register<Emblem, IBrush?>(nameof(ToolBrush));

    public static readonly StyledProperty<IBrush?> FlagBrushProperty =
        AvaloniaProperty.Register<Emblem, IBrush?>(nameof(FlagBrush));

    /// <summary>Поле внутри обода. Без него знак — дырка, а не кнопка.</summary>
    public static readonly StyledProperty<IBrush?> FieldBrushProperty =
        AvaloniaProperty.Register<Emblem, IBrush?>(nameof(FieldBrush));

    /// <summary>
    /// Чем отбивается молот от лезвия. Отдельно от поля намеренно: поле может
    /// быть полупрозрачным, а отбивка обязана быть плотной — иначе серп и молот,
    /// у которых один цвет, сливаются в пятно.
    /// </summary>
    public static readonly StyledProperty<IBrush?> CutBrushProperty =
        AvaloniaProperty.Register<Emblem, IBrush?>(nameof(CutBrush));

    public static readonly StyledProperty<IBrush?> LetterBrushProperty =
        AvaloniaProperty.Register<Emblem, IBrush?>(nameof(LetterBrush));

    // ── Как движется ──

    /// <summary>Идёт попытка подключения: механизм дёргается, будто заело.</summary>
    public static readonly StyledProperty<bool> IsJammedProperty =
        AvaloniaProperty.Register<Emblem, bool>(nameof(IsJammed));

    /// <summary>Туннель поднят: механизм ровно вращается.</summary>
    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<Emblem, bool>(nameof(IsRunning));

    public IBrush? FrameBrush { get => GetValue(FrameBrushProperty); set => SetValue(FrameBrushProperty, value); }
    public IBrush? ToolBrush { get => GetValue(ToolBrushProperty); set => SetValue(ToolBrushProperty, value); }
    public IBrush? FlagBrush { get => GetValue(FlagBrushProperty); set => SetValue(FlagBrushProperty, value); }
    public IBrush? FieldBrush { get => GetValue(FieldBrushProperty); set => SetValue(FieldBrushProperty, value); }
    public IBrush? CutBrush { get => GetValue(CutBrushProperty); set => SetValue(CutBrushProperty, value); }
    public IBrush? LetterBrush { get => GetValue(LetterBrushProperty); set => SetValue(LetterBrushProperty, value); }
    public bool IsJammed { get => GetValue(IsJammedProperty); set => SetValue(IsJammedProperty, value); }
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    static Emblem()
    {
        AffectsRender<Emblem>(
            FrameBrushProperty, ToolBrushProperty, FlagBrushProperty,
            FieldBrushProperty, CutBrushProperty, LetterBrushProperty);
    }

    // ═══════════════ Разметка знака ═══════════════
    // Всё в системе 200 на 200 и потом масштабируется под место.

    private const double Side = 200;
    private const double Cx = 100;      // центр шестерни
    private const double Cy = 98;
    private const double RimOuter = 76;
    private const double RimInner = 62;
    private const int Teeth = 22;

    private static readonly Geometry GearRing = BuildGear();
    private static readonly Geometry Sheaf = BuildSheaf();
    private static readonly Geometry Plaque = BuildPlaque();
    private static readonly Geometry StarShape = Build(Star(Cx, 180, 11.5, 4.8));
    private static readonly Geometry FlagPole = BuildPole();
    private static readonly Geometry FlagCloth = BuildCloth();
    private static readonly Geometry Sickle = BuildSickle();
    private static readonly Geometry HammerUnder = BuildHammer(1.8, 6.8, 17.0, 9.0);
    private static readonly Geometry Hammer = BuildHammer(0, 5.0, 15.2, 7.4);

    private static readonly Typeface LetterFace =
        new("Segoe UI", FontStyle.Normal, FontWeight.Bold);

    // ═══════════════ Движение ═══════════════

    private DispatcherTimer? _timer;
    private double _angle;              // градусы, накапливаются
    private double _speed;              // градусов в секунду
    private double _phase;              // время внутри режима, секунды

    private const double RunSpeed = 18;         // один оборот примерно за двадцать секунд
    private const double JamDrift = 8;          // заедающий механизм почти не продвигается
    private const double JamSwing = 150;        // зато дёргается заметно
    private const double JamHz = 1.6;
    private const double FrameSeconds = 0.025;  // сорок кадров в секунду хватает

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateMotion();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopTimer();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsJammedProperty || change.Property == IsRunningProperty)
        {
            _phase = 0;
            UpdateMotion();
        }
    }

    /// <summary>
    /// Таймер живёт только пока есть движение. Знак в покое ничего не считает и
    /// не перерисовывается — иначе окно, открытое на сутки, грело бы процессор
    /// ради неподвижной картинки.
    /// </summary>
    private void UpdateMotion()
    {
        if (IsJammed || IsRunning) StartTimer();
        else if (Math.Abs(_speed) < 0.4) StopTimer();
        else StartTimer();   // даём затормозить плавно, потом остановимся сами
    }

    private void StartTimer()
    {
        if (_timer is not null) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(FrameSeconds) };
        _timer.Tick += (_, _) => Advance(FrameSeconds);
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>
    /// Шаг движения. Скорость не переключается рывком, а подтягивается к цели —
    /// поэтому переход «дёргается → крутится» выглядит разгоном, а отключение
    /// выбегом, а не обрывом.
    /// </summary>
    private void Advance(double dt)
    {
        _phase += dt;

        double target;
        double tau;     // за сколько скорость подтягивается к цели

        if (IsJammed)
        {
            // Куб синуса вместо синуса: рывок резкий, а между рывками механизм
            // почти замирает — именно так и ведёт себя то, что заело.
            var swing = Math.Pow(Math.Sin(2 * Math.PI * JamHz * _phase), 3);
            target = JamDrift + JamSwing * swing;
            tau = 0.05;
        }
        else if (IsRunning)
        {
            target = RunSpeed;
            tau = 0.9;
        }
        else
        {
            target = 0;
            tau = 0.7;
        }

        _speed += (target - _speed) * (1 - Math.Exp(-dt / tau));
        _angle += _speed * dt;

        if (_angle >= 360 || _angle <= -360) _angle %= 360;

        InvalidateVisual();

        // Остановились — выключаем таймер до следующего повода.
        if (!IsJammed && !IsRunning && Math.Abs(_speed) < 0.4) StopTimer();
    }

    // ═══════════════ Отрисовка ═══════════════

    /// <summary>
    /// Знак всегда квадратный и занимает всё, что дают. Без этого он просит ноль
    /// и пропадает: место, куда его ставят, содержимое обычно не растягивает, а
    /// выравнивает по запрошенному размеру.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(side) || double.IsNaN(side) || side <= 0) side = Side;

        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 2) return;

        var scale = size / Side;
        var dx = (Bounds.Width - size) / 2;
        var dy = (Bounds.Height - size) / 2;

        using var _ = context.PushTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy));

        var frame = FrameBrush;
        var tool = ToolBrush;
        var flag = FlagBrush;

        // Поле под ободом: превращает знак из наклейки в кнопку с оправой.
        if (FieldBrush is { } field)
            context.DrawEllipse(field, null, new Point(Cx, Cy), RimInner + 1, RimInner + 1);

        if (frame is not null)
        {
            context.DrawGeometry(frame, null, Sheaf);

            // Вращается только механизм. Флаг, картуш и инструменты к нему не
            // прикручены и стоят на месте — иначе знак не крутился бы, а просто
            // вертелся целиком, и смысл движения пропал бы.
            using (context.PushTransform(RotationAt(new Point(Cx, Cy), _angle)))
                context.DrawGeometry(frame, null, GearRing);

            context.DrawGeometry(frame, null, Plaque);
        }

        if (tool is not null) context.DrawGeometry(tool, null, StarShape);

        if (flag is not null)
        {
            context.DrawGeometry(flag, null, FlagPole);
            context.DrawGeometry(flag, null, FlagCloth);
        }

        DrawLetters(context);

        if (tool is not null)
        {
            context.DrawGeometry(tool, null, Sickle);

            if (CutBrush is { } under) context.DrawGeometry(under, null, HammerUnder);
            context.DrawGeometry(tool, null, Hammer);
        }
    }

    /// <summary>
    /// Надпись на полотнище. Буквы ставятся поштучно: разрядка в плакатной
    /// типографике обязательна, а вывод строки её не задаёт.
    /// </summary>
    private void DrawLetters(DrawingContext context)
    {
        if (LetterBrush is not { } brush) return;

        // Ниже этого размера буквы всё равно превратятся в грязь, а грязь на
        // знаке хуже её отсутствия.
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 90) return;

        const string word = "СССР";
        const double step = 21;
        const double origin = 118;

        using var _ = context.PushTransform(RotationAt(new Point(origin, 40), -3));

        for (var i = 0; i < word.Length; i++)
        {
            var text = new FormattedText(
                word[i].ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LetterFace, 27, brush);

            var x = origin + (i - (word.Length - 1) / 2.0) * step - text.Width / 2;
            context.DrawText(text, new Point(x, 40 - text.Height / 2));
        }
    }

    private static Matrix RotationAt(Point centre, double degrees) =>
        Matrix.CreateTranslation(-centre.X, -centre.Y)
        * Matrix.CreateRotation(degrees * Math.PI / 180)
        * Matrix.CreateTranslation(centre.X, centre.Y);

    // ═══════════════ Построение фигур ═══════════════

    private static Geometry Build(params IReadOnlyList<Point>[] contours)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var contour in contours)
            {
                if (contour.Count < 3) continue;

                ctx.BeginFigure(contour[0], isFilled: true);
                for (var i = 1; i < contour.Count; i++) ctx.LineTo(contour[i]);
                ctx.EndFigure(true);
            }
        }
        return geometry;
    }

    private static Point Polar(double cx, double cy, double r, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(cx + r * Math.Cos(a), cy - r * Math.Sin(a));
    }

    /// <summary>Кольцо: наружу вперёд, внутрь обратно.</summary>
    private static List<Point> Band(double cx, double cy, double rOut, double rIn, double from, double to)
    {
        var pts = new List<Point>();
        var step = to > from ? 3.0 : -3.0;

        for (var a = from; step > 0 ? a <= to : a >= to; a += step) pts.Add(Polar(cx, cy, rOut, a));
        for (var a = to; step > 0 ? a >= from : a <= from; a -= step) pts.Add(Polar(cx, cy, rIn, a));

        return pts;
    }

    /// <summary>Полоса вдоль оси: центр, угол, полудлина, полуширина.</summary>
    private static List<Point> Bar(double cx, double cy, double degrees, double half, double wide)
    {
        var a = degrees * Math.PI / 180;
        double ux = Math.Cos(a), uy = -Math.Sin(a);
        double vx = -uy, vy = ux;

        return
        [
            new Point(cx - ux * half - vx * wide, cy - uy * half - vy * wide),
            new Point(cx + ux * half - vx * wide, cy + uy * half - vy * wide),
            new Point(cx + ux * half + vx * wide, cy + uy * half + vy * wide),
            new Point(cx - ux * half + vx * wide, cy - uy * half + vy * wide),
        ];
    }

    /// <summary>Зерно: заострено с обоих концов.</summary>
    private static List<Point> Grain(Point from, Point to, double wide)
    {
        const int steps = 12;
        var pts = new List<Point>();

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / len, uy = dy / len;
        double vx = -uy, vy = ux;

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var half = wide * Math.Pow(Math.Sin(Math.PI * t), 0.72) / 2;
            pts.Add(new Point(from.X + ux * len * t + vx * half, from.Y + uy * len * t + vy * half));
        }
        for (var i = steps; i >= 0; i--)
        {
            var t = i / (double)steps;
            var half = wide * Math.Pow(Math.Sin(Math.PI * t), 0.72) / 2;
            pts.Add(new Point(from.X + ux * len * t - vx * half, from.Y + uy * len * t - vy * half));
        }

        return pts;
    }

    private static List<Point> Star(double cx, double cy, double outer, double inner)
    {
        var pts = new List<Point>();
        for (var i = 0; i < 10; i++)
            pts.Add(Polar(cx, cy, i % 2 == 0 ? outer : inner, 90 + i * 36));

        return pts;
    }

    private static Geometry BuildGear()
    {
        var contours = new List<IReadOnlyList<Point>> { Band(Cx, Cy, RimOuter, RimInner, 0, 360) };

        for (var i = 0; i < Teeth; i++)
        {
            var a = i * (360.0 / Teeth);
            contours.Add(
            [
                Polar(Cx, Cy, RimOuter - 2, a - 5.6),
                Polar(Cx, Cy, RimOuter + 9, a - 3.8),
                Polar(Cx, Cy, RimOuter + 9, a + 3.8),
                Polar(Cx, Cy, RimOuter - 2, a + 5.6),
            ]);
        }

        return Build(contours.ToArray());
    }

    /// <summary>
    /// Колосья веером из-под нижнего края шестерни. Из-под самого картуша они
    /// читались усами, торчащими из таблички.
    /// </summary>
    private static Geometry BuildSheaf()
    {
        var contours = new List<IReadOnlyList<Point>>();

        foreach (var side in new[] { -1, 1 })
        {
            var baseX = Cx + side * 21;
            const double baseY = 176;

            for (var i = 0; i < 4; i++)
            {
                var a = 152.0 + i * 13.0;
                var angle = side < 0 ? a : 180 - a;
                var len = 30.0 - i * 3.0;
                contours.Add(Grain(new Point(baseX, baseY), Polar(baseX, baseY, len, angle), 5.2));
            }
        }

        return Build(contours.ToArray());
    }

    private static Geometry BuildPole()
    {
        const double x1 = 58, y1 = 160, x2 = 72, y2 = 14;
        var angle = Math.Atan2(-(y2 - y1), x2 - x1) * 180 / Math.PI;
        var half = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) / 2;

        return Build(Bar((x1 + x2) / 2, (y1 + y2) / 2, angle, half, 2.8));
    }

    /// <summary>Полотнище: края волнами, иначе флаг выглядит доской.</summary>
    private static Geometry BuildCloth()
    {
        const double x = 70, y = 20, w = 98, h = 42;
        var pts = new List<Point>();

        for (var i = 0; i <= 24; i++)
        {
            var t = i / 24.0;
            pts.Add(new Point(x + w * t, y + 5.0 * Math.Sin(t * 5.6) - 2.5 * t));
        }
        for (var i = 24; i >= 0; i--)
        {
            var t = i / 24.0;
            pts.Add(new Point(x + w * t, y + h + 5.4 * Math.Sin(t * 5.6 + 0.5) - 2.5 * t));
        }

        return Build(pts);
    }

    /// <summary>
    /// Серп. Серпом, а не скобкой, его делают две вещи: радиус растёт от рукояти
    /// к острию — лезвие раскручивается спиралью, а не идёт по окружности, — и
    /// толщина сходит на нет к самому кончику, оставаясь полной у рукояти.
    /// </summary>
    private static Geometry BuildSickle()
    {
        const double cx = Cx + 3, cy = Cy + 4;
        const double r1 = 29, r2 = 37, a1 = -148, a2 = 88, w1 = 13.5, w2 = 1.5;
        const int steps = 54;

        var blade = new List<Point>();
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            blade.Add(Polar(cx, cy, r1 + (r2 - r1) * t, a1 + (a2 - a1) * t));
        }
        for (var i = steps; i >= 0; i--)
        {
            var t = i / (double)steps;
            var r = r1 + (r2 - r1) * t;
            var w = w2 + (w1 - w2) * (1 - Math.Pow(t, 2.5)) * (0.76 + 0.3 * Math.Sin(Math.PI * t));
            blade.Add(Polar(cx, cy, r - w, a1 + (a2 - a1) * t));
        }

        // Рукоять прямая и длинная: каплей она читалась листиком, а не ручкой.
        var heel = Polar(cx, cy, r1, a1);
        const double handleAngle = 214, handleLen = 30;
        var mid = Polar(heel.X, heel.Y, handleLen / 2, handleAngle);

        return Build(blade, Bar(mid.X, mid.Y, handleAngle, handleLen / 2, 4.8));
    }

    private static Geometry BuildHammer(double grow, double handleWide, double headHalf, double headWide)
    {
        const double hx = Cx - 21, hy = Cy - 22, tx = Cx + 15, ty = Cy + 34;

        var angle = Math.Atan2(-(ty - hy), tx - hx) * 180 / Math.PI;
        var half = Math.Sqrt((tx - hx) * (tx - hx) + (ty - hy) * (ty - hy)) / 2;

        return Build(
            Bar((hx + tx) / 2, (hy + ty) / 2, angle, half + grow, handleWide),
            Bar(hx, hy, angle + 90, headHalf, headWide));
    }

    private static Geometry BuildPlaque() => Build(new List<Point>
    {
        new(Cx - 20, 168), new(Cx + 20, 168), new(Cx + 16, 192), new(Cx - 16, 192),
    });
}
