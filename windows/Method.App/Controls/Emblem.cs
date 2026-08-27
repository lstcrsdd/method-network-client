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

/// <summary>Чем обрамлён механизм.</summary>
public enum EmblemWreath
{
    /// <summary>Колос: зёрна лежат вдоль стебля и налегают друг на друга.</summary>
    Wheat,

    /// <summary>Лавр: крупные листья с промежутками, без остей.</summary>
    Laurel,

    /// <summary>Дуб: лопастные листья, самый тяжёлый и «орденский».</summary>
    Oak,

    /// <summary>Ничего: голая шестерня.</summary>
    None,
}

/// <summary>
/// Знак на кнопке подключения: шестерня в венке колосьев, флаг, серп и молот,
/// звезда в картуше.
///
/// <para><b>Почему рисуем кодом, а не строками путей.</b> Знак собран из
/// полутора сотен фигур — дуг, зубцов, зёрен. Выгруженный в разметку он был бы
/// килобайтами координат, в которых нельзя ни поправить толщину лезвия, ни
/// добавить зубец. Здесь он собирается из параметров: ширина обода, число
/// зубцов, изгиб серпа — это числа, которые видно и можно менять.</para>
///
/// <para><b>Почему всё обведено.</b> Заливка без контура читается плашкой:
/// фигуры одного тона сливаются в пятно, и знак выглядит нарисованным мышью.
/// Печатный знак держится на канте — латунный по оправе (шестерня, колосья,
/// картуш) и тёмный по внутренним фигурам (флаг, инструменты, звезда). Кант
/// делает две вещи сразу: отделяет фигуру от фигуры и очерчивает силуэт на
/// тёмном поле, где одна заливка растворилась бы.</para>
///
/// <para><b>Почему шестерня меньше поля.</b> Венок обязан идти снаружи обода:
/// колосья поверх зубцов — это каша, а короткий пучок из-под низа читается
/// щетиной, а не хлебом. Поэтому механизм ужат, и кольцо между ним и краем
/// отдано колосьям.</para>
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

    /// <summary>Серп, молот и звезда — единственное яркое пятно. Он же кант оправы.</summary>
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

    /// <summary>
    /// Тёмный кант и внутренняя проработка: складки полотнища, огранка звезды,
    /// отверстия обода. Держится отдельно от отбивки, потому что отбивке
    /// положено быть плотной, а канту — просто тёмным.
    /// </summary>
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<Emblem, IBrush?>(nameof(OutlineBrush));

    // ── Как движется ──

    /// <summary>Идёт попытка подключения: механизм дёргается, будто заело.</summary>
    public static readonly StyledProperty<bool> IsJammedProperty =
        AvaloniaProperty.Register<Emblem, bool>(nameof(IsJammed));

    /// <summary>Туннель поднят: механизм ровно вращается.</summary>
    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<Emblem, bool>(nameof(IsRunning));

    /// <summary>
    /// Курсор на кнопке, туннель ещё не поднят: механизм мелко подрагивает —
    /// показывает, что он живой и его можно тронуть, до того как его тронули.
    /// </summary>
    public static readonly StyledProperty<bool> IsNudgedProperty =
        AvaloniaProperty.Register<Emblem, bool>(nameof(IsNudged));

    /// <summary>Чем обрамлён механизм.</summary>
    public static readonly StyledProperty<EmblemWreath> WreathProperty =
        AvaloniaProperty.Register<Emblem, EmblemWreath>(nameof(Wreath));

    public EmblemWreath Wreath { get => GetValue(WreathProperty); set => SetValue(WreathProperty, value); }

    public IBrush? FrameBrush { get => GetValue(FrameBrushProperty); set => SetValue(FrameBrushProperty, value); }
    public IBrush? ToolBrush { get => GetValue(ToolBrushProperty); set => SetValue(ToolBrushProperty, value); }
    public IBrush? FlagBrush { get => GetValue(FlagBrushProperty); set => SetValue(FlagBrushProperty, value); }
    public IBrush? FieldBrush { get => GetValue(FieldBrushProperty); set => SetValue(FieldBrushProperty, value); }
    public IBrush? CutBrush { get => GetValue(CutBrushProperty); set => SetValue(CutBrushProperty, value); }
    public IBrush? LetterBrush { get => GetValue(LetterBrushProperty); set => SetValue(LetterBrushProperty, value); }
    public IBrush? OutlineBrush { get => GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty, value); }
    public bool IsJammed { get => GetValue(IsJammedProperty); set => SetValue(IsJammedProperty, value); }
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public bool IsNudged { get => GetValue(IsNudgedProperty); set => SetValue(IsNudgedProperty, value); }

    static Emblem()
    {
        AffectsRender<Emblem>(
            FrameBrushProperty, ToolBrushProperty, FlagBrushProperty,
            FieldBrushProperty, CutBrushProperty, LetterBrushProperty,
            OutlineBrushProperty, WreathProperty);
    }

    // ═══════════════ Разметка знака ═══════════════
    // Всё в системе 200 на 200 и потом масштабируется под место.

    private const double Side = 200;
    private const double Cx = 100;      // центр шестерни
    private const double Cy = 98;
    private const double RimOuter = 66;
    private const double RimInner = 53;
    private const double ToothRise = 6.5;
    private const int Teeth = 22;

    /// <summary>
    /// Отсчёт зубцов и отверстий ведём от верхней точки, а не от правой. Знак
    /// симметричен относительно вертикали — венок, картуш, звезда, — и обод
    /// обязан ей подчиняться: при отсчёте от нуля ось проходила мимо и зубцов, и
    /// отверстий, отчего ровный по шагу ряд читался расставленным наугад.
    /// </summary>
    private const double TopSpoke = 90;

    /// <summary>По этой окружности идут стебли венка — снаружи зубцов.</summary>
    private const double WheatRadius = 84;

    private static readonly Geometry GearRing = BuildGear();
    private static readonly Geometry GearGroove = BuildGroove();
    private static readonly Geometry GearHoles = BuildHoles();
    private static readonly Geometry WheatLeaves = BuildWheat();
    private static readonly Geometry WheatAwns = BuildAwns();
    private static readonly Geometry LaurelLeaves = BuildLaurel();
    private static readonly Geometry OakLeaves = BuildOak();
    private static readonly Geometry Plaque = BuildPlaque();
    private static readonly Geometry StarShape = Build(Star(Cx, 180, 11.5, 4.8));
    private static readonly Geometry StarFacets = BuildFacets();
    private static readonly Geometry FlagPole = BuildPole();
    private static readonly Geometry FlagFinial = BuildFinial();
    private static readonly Geometry FlagCloth = BuildCloth();
    private static readonly Geometry ClothFolds = BuildFolds();
    private static readonly Geometry Sickle = BuildSickle();
    private static readonly Geometry HammerUnder = BuildHammer(1.5, 5.8, 14.5, 7.7);
    private static readonly Geometry Hammer = BuildHammer(0, 4.3, 13.0, 6.3);

    private static readonly Typeface LetterFace =
        new("Segoe UI", FontStyle.Normal, FontWeight.Bold);

    // ── Перья ──
    // Собираются один раз на смену кистей, а не на кадр: при вращении кадров
    // сорок в секунду, и новое перо на каждый из них — мусор на ровном месте.

    private IPen? _gild;        // латунный кант оправы
    private IPen? _gildWreath;  // кант венка — тоньше, чем у оправы
    private IPen? _gildThin;    // ости колосьев
    private IPen? _edge;        // тёмный кант внутренних фигур
    private IPen? _hair;        // проработка: складки, огранка, фаска
    private bool _pensReady;

    // ═══════════════ Движение ═══════════════

    private DispatcherTimer? _timer;
    private double _angle;              // градусы, накапливаются
    private double _speed;              // градусов в секунду
    private double _phase;              // время внутри режима, секунды

    private const double RunSpeed = 9;          // один оборот примерно за сорок секунд
    private const double JamDrift = 3;          // заедающий механизм почти не продвигается
    private const double JamSwing = 130;        // зато дёргается заметно
    private const double JamHz = 1.35;

    /// <summary>
    /// Какую долю рывка механизм отыгрывает назад. Равные ходы туда-обратно —
    /// это качели, а заевшее колесо бьётся вперёд и лишь слегка отдаёт.
    /// </summary>
    private const double JamRecoil = 0.18;
    private const double NudgeSwing = 30;       // дрожь под курсором: около градуса
    private const double NudgeHz = 6.5;
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

        if (change.Property == IsJammedProperty || change.Property == IsRunningProperty
            || change.Property == IsNudgedProperty)
        {
            _phase = 0;
            UpdateMotion();
        }
        else if (change.Property == ToolBrushProperty || change.Property == OutlineBrushProperty)
        {
            _pensReady = false;
        }
    }

    /// <summary>
    /// Таймер живёт только пока есть движение. Знак в покое ничего не считает и
    /// не перерисовывается — иначе окно, открытое на сутки, грело бы процессор
    /// ради неподвижной картинки.
    /// </summary>
    private void UpdateMotion()
    {
        if (IsJammed || IsRunning || IsNudged) StartTimer();
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
            // Пятая степень синуса вместо самого синуса: удар короткий и резкий,
            // а между ударами механизм почти замирает — именно так и ведёт себя
            // то, что заело. Ход назад придавлен: он нужен как отдача от удара,
            // а в полную силу превращает заедание в маятник.
            var wave = Math.Pow(Math.Sin(2 * Math.PI * JamHz * _phase), 5);
            var swing = wave > 0 ? wave : wave * JamRecoil;

            target = JamDrift + JamSwing * swing;
            tau = 0.05;
        }
        else if (IsRunning)
        {
            target = RunSpeed;
            tau = 0.9;
        }
        else if (IsNudged)
        {
            // Дрожь, а не ход: колебание быстрое и мелкое, около градуса в обе
            // стороны. Оно обязано читаться откликом на курсор, а не началом
            // работы — механизм ещё не пущен, и делать вид, что пущен, нельзя.
            target = NudgeSwing * Math.Sin(2 * Math.PI * NudgeHz * _phase);
            tau = 0.03;
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
        if (!IsJammed && !IsRunning && !IsNudged && Math.Abs(_speed) < 0.4) StopTimer();
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

        EnsurePens();

        var scale = size / Side;
        var dx = (Bounds.Width - size) / 2;
        var dy = (Bounds.Height - size) / 2;

        using var _ = context.PushTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy));

        var frame = FrameBrush;
        var tool = ToolBrush;
        var flag = FlagBrush;

        // Мелкую проработку на малом размере не рисуем: линия тоньше пикселя
        // превращается в грязь, а грязь на знаке хуже, чем её отсутствие.
        var detailed = size >= 96;

        // Поле под ободом: превращает знак из наклейки в кнопку с оправой.
        if (FieldBrush is { } field)
            context.DrawEllipse(field, null, new Point(Cx, Cy), RimInner + 1, RimInner + 1);

        if (frame is not null)
        {
            if (Wreath == EmblemWreath.Wheat && detailed)
                context.DrawGeometry(null, _gildThin, WheatAwns);

            if (WreathOf(Wreath) is { } wreath) context.DrawGeometry(frame, _gildWreath, wreath);

            // Вращается только механизм. Венок, флаг, картуш и инструменты к нему
            // не прикручены и стоят на месте — иначе знак не крутился бы, а просто
            // вертелся целиком, и смысл движения пропал бы.
            using (context.PushTransform(RotationAt(new Point(Cx, Cy), _angle)))
            {
                context.DrawGeometry(frame, _gild, GearRing);
                if (detailed)
                {
                    context.DrawGeometry(null, _hair, GearGroove);
                    context.DrawGeometry(OutlineBrush, null, GearHoles);
                }
            }

            context.DrawGeometry(frame, _gild, Plaque);
        }

        if (tool is not null)
        {
            context.DrawGeometry(tool, _edge, StarShape);
            if (detailed) context.DrawGeometry(null, _hair, StarFacets);
        }

        if (flag is not null)
        {
            // Древко кончается там, где его перекроет рукоять серпа: доведённое
            // до низа, оно рассекало знак пополам и обрывалось обрубком.
            context.DrawGeometry(flag, _edge, FlagPole);
            if (detailed) context.DrawGeometry(ToolBrush ?? flag, _edge, FlagFinial);

            context.DrawGeometry(flag, _edge, FlagCloth);
            if (detailed) context.DrawGeometry(null, _hair, ClothFolds);
        }

        DrawLetters(context);

        if (tool is not null)
        {
            context.DrawGeometry(tool, _edge, Sickle);

            if (CutBrush is { } under) context.DrawGeometry(under, null, HammerUnder);
            context.DrawGeometry(tool, _edge, Hammer);
        }
    }

    /// <summary>
    /// Перья по кистям. Толщины заданы в системе знака и масштабируются вместе с
    /// ним, поэтому кант остаётся одинаковой доли на любом размере.
    /// </summary>
    private void EnsurePens()
    {
        if (_pensReady) return;
        _pensReady = true;

        _gild = Pen(ToolBrush, 1.25);

        // Венку кант тоньше: зерно вдвое у́же зубца, и общая толщина съедала бы
        // в нём всю заливку — венок выходил сплошь латунным и спорил с оправой.
        _gildWreath = Pen(ToolBrush, 0.95);
        _gildThin = Pen(ToolBrush, 0.72);
        _edge = Pen(OutlineBrush, 1.3);
        _hair = Pen(OutlineBrush, 1.0);
    }

    private static IPen? Pen(IBrush? brush, double thickness) =>
        brush is null
            ? null
            : new Pen(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

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
        const double step = 20;
        const double origin = 116;
        const double line = 37;

        using var _ = context.PushTransform(RotationAt(new Point(origin, line), -3));

        for (var i = 0; i < word.Length; i++)
        {
            var text = new FormattedText(
                word[i].ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LetterFace, 25, brush);

            var x = origin + (i - (word.Length - 1) / 2.0) * step - text.Width / 2;
            context.DrawText(text, new Point(x, line - text.Height / 2));
        }
    }

    private static Matrix RotationAt(Point centre, double degrees) =>
        Matrix.CreateTranslation(-centre.X, -centre.Y)
        * Matrix.CreateRotation(degrees * Math.PI / 180)
        * Matrix.CreateTranslation(centre.X, centre.Y);

    // ═══════════════ Построение фигур ═══════════════

    private static Geometry Build(params IReadOnlyList<Point>[] contours) =>
        Build(FillRule.NonZero, contours);

    private static Geometry Build(FillRule rule, params IReadOnlyList<Point>[] contours)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.SetFillRule(rule);

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

    /// <summary>
    /// Незамкнутые линии: проработка, которую только обводят — ости, складки,
    /// огранка. Замкни их, и обводка вернулась бы сама к себе лишним штрихом.
    /// </summary>
    private static Geometry Strokes(params IReadOnlyList<Point>[] lines)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var line in lines)
            {
                if (line.Count < 2) continue;

                ctx.BeginFigure(line[0], isFilled: false);
                for (var i = 1; i < line.Count; i++) ctx.LineTo(line[i]);
                ctx.EndFigure(false);
            }
        }
        return geometry;
    }

    private static Point Polar(double cx, double cy, double r, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(cx + r * Math.Cos(a), cy - r * Math.Sin(a));
    }

    private static List<Point> Ring(double cx, double cy, double r, double step = 4)
    {
        var pts = new List<Point>();
        for (double a = 0; a < 360; a += step) pts.Add(Polar(cx, cy, r, a));
        return pts;
    }

    /// <summary>Дуговая полоса: наружу вперёд, внутрь обратно.</summary>
    private static List<Point> Band(double r, double half, double from, double to)
    {
        var pts = new List<Point>();
        var step = to > from ? 2.0 : -2.0;

        for (var a = from; step > 0 ? a <= to : a >= to; a += step) pts.Add(Polar(Cx, Cy, r + half, a));
        for (var a = to; step > 0 ? a >= from : a <= from; a -= step) pts.Add(Polar(Cx, Cy, r - half, a));

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

    /// <summary>
    /// Шестерня одной фигурой: наружный обвод с зубцами и вырезанное отверстие
    /// по чётно-нечётному правилу. Зубцы отдельными плашками поверх кольца
    /// заливаются так же, но кант тогда обводит каждую плашку по отдельности и
    /// расчерчивает обод поперёк — механизм рассыпается на детали.
    /// </summary>
    private static Geometry BuildGear()
    {
        var outer = new List<Point>();

        for (var i = 0; i < Teeth; i++)
        {
            var a = TopSpoke + i * (360.0 / Teeth);

            outer.Add(Polar(Cx, Cy, RimOuter, a - 7.0));
            outer.Add(Polar(Cx, Cy, RimOuter, a - 5.6));
            outer.Add(Polar(Cx, Cy, RimOuter + ToothRise, a - 3.8));
            outer.Add(Polar(Cx, Cy, RimOuter + ToothRise, a + 3.8));
            outer.Add(Polar(Cx, Cy, RimOuter, a + 5.6));
            outer.Add(Polar(Cx, Cy, RimOuter, a + 7.0));
        }

        return Build(FillRule.EvenOdd, outer, Ring(Cx, Cy, RimInner, 3));
    }

    /// <summary>Фаска по ободу: делит его на два пояса и даёт толщину.</summary>
    private static Geometry BuildGroove()
    {
        var ring = Ring(Cx, Cy, 61.5, 4);
        ring.Add(ring[0]);
        return Strokes(ring);
    }

    /// <summary>
    /// Отверстия в ободе — через зубец, а не под каждым: двадцать две дырки по
    /// кругу читаются перфорацией, одиннадцать — облегчённым колесом.
    /// </summary>
    private static Geometry BuildHoles()
    {
        var holes = new List<IReadOnlyList<Point>>();

        for (var i = 0; i < Teeth / 2; i++)
        {
            var c = Polar(Cx, Cy, 57.5, TopSpoke + i * (720.0 / Teeth));
            holes.Add(Ring(c.X, c.Y, 2.2, 30));
        }

        return Build(holes.ToArray());
    }

    /// <summary>
    /// Венок: два колоса по дуге вдоль борта шестерни, комлями под картуш.
    /// Колос — это стебель с зёрнами вдоль него, а не пучок из одной точки:
    /// пучок читается щетиной, сколько зёрен в него ни ставь.
    /// </summary>
    private static Geometry BuildWheat()
    {
        var parts = new List<IReadOnlyList<Point>>();

        foreach (var (from, to, sign) in Stalks())
        {
            parts.Add(Band(WheatRadius, 1.2, from, to));

            foreach (var (seat, tip) in Ears(from, to, sign, WheatCount, WheatSweep, 9.5, 1.6))
                parts.Add(Grain(seat, tip, 3.6));

            // Внутренний ряд той же длины и толщины, смещённый на полшага: зёрна
            // садятся в промежутки, а не напротив, и колос читается полным, а не
            // двойным. Наклон здесь чуть круче — на меньшем радиусе тот же угол
            // даёт меньший сдвиг вдоль стебля, и зерно вставало бы поперёк.
            foreach (var (seat, tip) in Ears(from, to, sign, WheatCount - 1, WheatSweep + 1, 9.5, 1.6, -1, 0.5, WheatCount - 1))
                parts.Add(Grain(seat, tip, 3.6));
        }

        return Build(parts.ToArray());
    }

    /// <summary>Ости: два волоска с кончика каждого зерна. Без них колос — овал.</summary>
    private static Geometry BuildAwns()
    {
        var lines = new List<IReadOnlyList<Point>>();

        foreach (var (from, to, sign) in Stalks())
            foreach (var (seat, tip) in Ears(from, to, sign, WheatCount, WheatSweep, 9.5, 1.6))
            {
                var away = Math.Atan2(-(tip.Y - seat.Y), tip.X - seat.X) * 180 / Math.PI;

                foreach (var spread in new[] { -8.0, 8.0 })
                    lines.Add(new[] { tip, Polar(tip.X, tip.Y, 6, away + spread) });
            }

        return Strokes(lines.ToArray());
    }

    /// <summary>
    /// Лавр: тех же посадок вдвое меньше, лист крупнее и длиннее, остей нет.
    /// Спокойнее колоса — и не спорит с шестернёй за внимание.
    /// </summary>
    private static Geometry BuildLaurel()
    {
        var parts = new List<IReadOnlyList<Point>>();

        foreach (var (from, to, sign) in Stalks())
        {
            parts.Add(Band(WheatRadius, 1.1, from, to));

            foreach (var (seat, tip) in Ears(from, to, sign, 8, 13, 15.0, 3.0))
                parts.Add(Grain(seat, tip, 6.4));

            foreach (var (seat, tip) in Ears(from, to, sign, 7, 14, 11.5, 2.0, -1, 0.5, 7))
                parts.Add(Grain(seat, tip, 6.0));
        }

        return Build(parts.ToArray());
    }

    /// <summary>Дуб: лист с лопастями. Тяжелее прочих — это венок ордена, не поля.</summary>
    private static Geometry BuildOak()
    {
        var parts = new List<IReadOnlyList<Point>>();

        foreach (var (from, to, sign) in Stalks())
        {
            parts.Add(Band(WheatRadius, 1.2, from, to));

            foreach (var (seat, tip) in Ears(from, to, sign, 7, 15, 15.5, 3.0))
                parts.Add(OakLeaf(seat, tip, 9.0));

            foreach (var (seat, tip) in Ears(from, to, sign, 6, 16, 11.0, 2.0, -1, 0.5, 6))
                parts.Add(OakLeaf(seat, tip, 8.0));
        }

        return Build(parts.ToArray());
    }

    private static Geometry? WreathOf(EmblemWreath kind) => kind switch
    {
        EmblemWreath.Wheat => WheatLeaves,
        EmblemWreath.Laurel => LaurelLeaves,
        EmblemWreath.Oak => OakLeaves,
        _ => null,
    };

    private const int WheatCount = 11;
    private const double WheatSweep = 13;

    /// <summary>Левый колос идёт от низа против часовой, правый — зеркально.</summary>
    private static IEnumerable<(double From, double To, int Sign)> Stalks()
    {
        yield return (263, 197, -1);
        yield return (277, 343, +1);
    }

    /// <summary>
    /// Посадки вдоль стебля. Главное здесь — <paramref name="sweep"/>: на сколько
    /// градусов дуги кончик уходит вперёд по стеблю. Малый угол ставит лист
    /// поперёк, и колос превращается в связку креветок; большой кладёт его почти
    /// вдоль, и соседние листья налегают друг на друга — то, чем колос и держится.
    /// </summary>
    private static IEnumerable<(Point Seat, Point Tip)> Ears(
        double from, double to, int sign, int count, double sweep, double length, double taper,
        int side = 1, double phase = 0, double grid = 0)
    {
        // Внутренний ряд идёт по сетке наружного, а не по своей: со своей его
        // последнее зерно уезжало за конец стебля и висело в воздухе.
        var span = grid > 0 ? grid : count - 1;

        for (var i = 0; i < count; i++)
        {
            var t = 0.04 + (i + phase) * (0.94 / span);
            var a = from + (to - from) * t;
            var len = length - taper * t;             // к вершине лист мельче

            yield return (
                Polar(Cx, Cy, WheatRadius + side * -0.6, a),
                Polar(Cx, Cy, WheatRadius + side * len, a + sign * sweep));
        }
    }

    /// <summary>
    /// Дубовый лист: тот же профиль, что у зерна, но края идут волной. Лопасти —
    /// это и есть весь дуб; гладкий овал читался бы лавром.
    /// </summary>
    private static List<Point> OakLeaf(Point from, Point to, double wide)
    {
        const int steps = 22;
        var pts = new List<Point>();

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / len, uy = dy / len;
        double vx = -uy, vy = ux;

        for (var side = 1; side >= -1; side -= 2)
        {
            var order = side > 0 ? Enumerable.Range(0, steps + 1) : Enumerable.Range(0, steps + 1).Reverse();

            foreach (var i in order)
            {
                var t = i / (double)steps;
                var half = wide * Math.Pow(Math.Sin(Math.PI * t), 0.55)
                                * (1 + 0.30 * Math.Sin(2 * Math.PI * 3.5 * t)) / 2;

                pts.Add(new Point(
                    from.X + ux * len * t + side * vx * half,
                    from.Y + uy * len * t + side * vy * half));
            }
        }

        return pts;
    }

    /// <summary>Огранка звезды: пять рёбер от центра к лучам.</summary>
    private static Geometry BuildFacets()
    {
        var lines = new List<IReadOnlyList<Point>>();
        var centre = new Point(Cx, 180);

        for (var i = 0; i < 5; i++)
            lines.Add(new[] { centre, Polar(Cx, 180, 11.5, 90 + i * 72) });

        return Strokes(lines.ToArray());
    }

    /// <summary>
    /// Древко обрывается на рукояти серпа, которая его и прячет. Доведённое до
    /// низа, оно делило поле пополам и торчало обрубком из-под обода.
    /// </summary>
    private static Geometry BuildPole()
    {
        const double x1 = 66, y1 = 126, x2 = 75, y2 = 9;
        var angle = Math.Atan2(-(y2 - y1), x2 - x1) * 180 / Math.PI;
        var half = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) / 2;

        return Build(Bar((x1 + x2) / 2, (y1 + y2) / 2, angle, half, 2.6));
    }

    /// <summary>Навершие: древко, обрубленное поперёк, выглядит палкой.</summary>
    private static Geometry BuildFinial() => Build(Ring(75.2, 6.4, 3.8, 20));

    /// <summary>Полотнище: края волнами, иначе флаг выглядит доской.</summary>
    private static Geometry BuildCloth()
    {
        var pts = new List<Point>();

        for (var i = 0; i <= 24; i++) pts.Add(ClothTop(i / 24.0));
        for (var i = 24; i >= 0; i--) pts.Add(ClothBottom(i / 24.0));

        return Build(pts);
    }

    /// <summary>
    /// Складки — только по краям полотнища. Посередине идёт надпись, и штрих
    /// поперёк неё превратил бы буквы в кашу.
    /// </summary>
    private static Geometry BuildFolds()
    {
        var lines = new List<IReadOnlyList<Point>>();

        foreach (var t in new[] { 0.05, 0.93 })
            lines.Add(new[] { ClothTop(t), ClothBottom(t) });

        return Strokes(lines.ToArray());
    }

    private const double ClothX = 72, ClothWidth = 92, ClothY = 17, ClothHeight = 40;

    private static Point ClothTop(double t) =>
        new(ClothX + ClothWidth * t, ClothY + 4.6 * Math.Sin(t * 5.6) - 2.5 * t);

    private static Point ClothBottom(double t) =>
        new(ClothX + ClothWidth * t, ClothY + ClothHeight + 5.0 * Math.Sin(t * 5.6 + 0.5) - 2.5 * t);

    /// <summary>
    /// Серп. Серпом, а не скобкой, его делают две вещи: радиус растёт от рукояти
    /// к острию — лезвие раскручивается спиралью, а не идёт по окружности, — и
    /// толщина сходит на нет к самому кончику, оставаясь полной у рукояти.
    /// </summary>
    private static Geometry BuildSickle()
    {
        const int steps = 54;

        var blade = new List<Point>();
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            blade.Add(Polar(SickleCx, SickleCy, SickleR1 + (SickleR2 - SickleR1) * t, SickleA1 + (SickleA2 - SickleA1) * t));
        }
        for (var i = steps; i >= 0; i--)
        {
            var t = i / (double)steps;
            var r = SickleR1 + (SickleR2 - SickleR1) * t;
            var w = 1.3 + (11.5 - 1.3) * (1 - Math.Pow(t, 2.5)) * (0.76 + 0.3 * Math.Sin(Math.PI * t));
            blade.Add(Polar(SickleCx, SickleCy, r - w, SickleA1 + (SickleA2 - SickleA1) * t));
        }

        // Рукоять прямая и длинная: каплей она читалась листиком, а не ручкой.
        var heel = Polar(SickleCx, SickleCy, SickleR1, SickleA1);
        var mid = Polar(heel.X, heel.Y, HandleLen / 2, HandleAngle);

        return Build(blade, Bar(mid.X, mid.Y, HandleAngle, HandleLen / 2, 4.1));
    }

    private const double SickleCx = Cx + 2.6, SickleCy = Cy + 3.4;
    private const double SickleR1 = 24.8, SickleR2 = 31.6, SickleA1 = -148, SickleA2 = 88;
    private const double HandleAngle = 214, HandleLen = 26;

    private static Geometry BuildHammer(double grow, double handleWide, double headHalf, double headWide)
    {
        var angle = Math.Atan2(-(HammerTy - HammerHy), HammerTx - HammerHx) * 180 / Math.PI;
        var half = Math.Sqrt(
            (HammerTx - HammerHx) * (HammerTx - HammerHx) +
            (HammerTy - HammerHy) * (HammerTy - HammerHy)) / 2;

        return Build(
            Bar((HammerHx + HammerTx) / 2, (HammerHy + HammerTy) / 2, angle, half + grow, handleWide),
            Bar(HammerHx, HammerHy, angle + 90, headHalf, headWide));
    }

    private const double HammerHx = Cx - 18, HammerHy = Cy - 19, HammerTx = Cx + 13, HammerTy = Cy + 29;

    private static Geometry BuildPlaque() => Build(new List<Point>
    {
        new(Cx - 20, 168), new(Cx + 20, 168), new(Cx + 16, 192), new(Cx - 16, 192),
    });
}
