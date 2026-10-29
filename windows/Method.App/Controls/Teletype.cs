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
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Method.App.Services;

namespace Method.App.Controls;

/// <summary>
/// Печатающая лента: выводит журнал так, как это делал терминал — по одному
/// знаку, с курсором на конце.
///
/// <para>Своя отрисовка, а не список надписей с анимацией: печать идёт по одному
/// знаку, и каждому нужен свой кадр. Список из полусотни надписей, у каждой из
/// которых по таймеру меняется текст, — это полсотни перестроений разметки в
/// секунду ради того, что здесь делается одним проходом по строкам.</para>
///
/// <para>Раскладка считается в точках, а не в знакоместах. Знакоместа были проще,
/// но привязывали ленту к моноширинному шрифту — единственному такому в
/// интерфейсе, из-за чего она выпадала из общего набора. Теперь строка кладётся
/// по измеренной ширине: название слева, ответ прижат к правому краю своей
/// колонки, между ними точки-выноски, метка исхода — у самого края.</para>
///
/// <para>Шрифт лента берёт у родителя, как обычная надпись: единый набор
/// задаётся один раз наверху, а не повторяется в каждом месте.</para>
/// </summary>
public sealed class Teletype : Control
{
    /// <summary>Знаков за кадр. Кадр — примерно шестьдесят раз в секунду.</summary>
    private const int PerFrame = 6;

    /// <summary>Пауза между строками, в кадрах: набор идёт очередями, а не сплошняком.</summary>
    private const int Pause = 3;

    /// <summary>Левая граница колонки ответов — доля ширины ленты.</summary>
    private const double AnswerColumn = 0.42;

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<Teletype>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<Teletype, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<Teletype, IBrush?>(nameof(Accent));

    public static readonly StyledProperty<IBrush?> WarningProperty =
        AvaloniaProperty.Register<Teletype, IBrush?>(nameof(Warning));

    public static readonly StyledProperty<IBrush?> MutedProperty =
        AvaloniaProperty.Register<Teletype, IBrush?>(nameof(Muted));

    public static readonly StyledProperty<IBrush?> DoneProperty =
        AvaloniaProperty.Register<Teletype, IBrush?>(nameof(Done));

    public static readonly StyledProperty<IBrush?> ErrorProperty =
        AvaloniaProperty.Register<Teletype, IBrush?>(nameof(Error));

    public static readonly StyledProperty<double> TextSizeProperty =
        AvaloniaProperty.Register<Teletype, double>(nameof(TextSize), 12.5);

    /// <summary>
    /// Сколько строк держать на виду. Ноль — сколько влезет по высоте: лента не
    /// должна вылезать за отведённое ей место, а высоту ей задаёт разметка.
    /// </summary>
    public static readonly StyledProperty<int> MaxLinesProperty =
        AvaloniaProperty.Register<Teletype, int>(nameof(MaxLines));

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Цвет удачного исхода и курсора.</summary>
    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public IBrush? Warning
    {
        get => GetValue(WarningProperty);
        set => SetValue(WarningProperty, value);
    }

    /// <summary>
    /// Цвет метки «ГОТОВ». Не задан — берётся акцент. Отдельный, потому что
    /// удачный исход — самая частая метка ленты: в плакатном оформлении акцент
    /// красный, и столбец красных «ГОТОВ» читается чередой тревог.
    /// </summary>
    public IBrush? Done
    {
        get => GetValue(DoneProperty);
        set => SetValue(DoneProperty, value);
    }

    /// <summary>
    /// Цвет метки «ОТКАЗ». Не задан — берётся цвет предупреждения. Отказ должен
    /// отличаться от «внимания» с одного взгляда, а не только по слову.
    /// </summary>
    public IBrush? Error
    {
        get => GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    /// <summary>Цвет точек-выносок между названием шага и ответом.</summary>
    public IBrush? Muted
    {
        get => GetValue(MutedProperty);
        set => SetValue(MutedProperty, value);
    }

    public double TextSize
    {
        get => GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    public int MaxLines
    {
        get => GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    /// <summary>Всё, что было в очереди, напечатано.</summary>
    public event Action? Drained;

    /// <summary>Печатать больше нечего.</summary>
    public bool IsIdle => _queue.Count == 0;

    private readonly List<JournalLine> _queue = [];
    private readonly List<JournalLine> _printed = [];
    private readonly DispatcherTimer _timer;

    private int _typed;
    private int _idle;
    private int _blink;

    static Teletype() =>
        AffectsRender<Teletype>(
            FontFamilyProperty, ForegroundProperty, AccentProperty,
            WarningProperty, MutedProperty, DoneProperty, ErrorProperty,
            TextSizeProperty, MaxLinesProperty);

    public Teletype()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Advance();
    }

    /// <summary>Высота строки. Считается от кегля: лента бывает крупной и мелкой.</summary>
    private double LineHeight => Math.Round(TextSize * 1.65);

    /// <summary>
    /// Сколько места лента просит.
    ///
    /// <para>Без этого она берёт столько, сколько дадут, и в раскладке с
    /// автоматической высотой схлопывается в одну строку — а рядом стоящий блок
    /// забирает всё остальное. Так и вышло, когда под лентой появился бегущий
    /// код: журнал показывал две записи из девяти, и выглядело это не теснотой,
    /// а тем, что клиент ничего не делал.</para>
    ///
    /// <para>Просит она ровно под свои строки, не больше. Дадут меньше — покажет
    /// хвост: предел высоты для неё жёсткий, вылезать из колонки некуда.</para>
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = _printed.Count + (_queue.Count > 0 ? 1 : 0);
        if (MaxLines > 0) rows = Math.Min(MaxLines, rows);

        var height = Math.Max(LineHeight, rows * LineHeight);
        if (!double.IsInfinity(availableSize.Height)) height = Math.Min(height, availableSize.Height);

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    private Typeface Face => new(FontFamily);

    /// <summary>Метки сравниваем без оглядки на регистр: печатаем-то прописными.</summary>
    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Поставить строку в очередь на печать.</summary>
    public void Enqueue(JournalLine line)
    {
        // Повтор последней строки: печатать его заново не надо, у строки на
        // месте только растёт счётчик.
        if (line.Times > 1)
        {
            if (_queue.Count > 0 && _queue[^1].SameAs(line))
            {
                _queue[^1] = line;
                return;
            }

            if (_queue.Count == 0 && _printed.Count > 0 && _printed[^1].SameAs(line))
            {
                _printed[^1] = line;
                InvalidateVisual();
                return;
            }
        }

        // Ответ, дописанный к уже напечатанному шагу, перепечатывать не надо:
        // строка стоит на своём месте, меняется только её хвост.
        var printed = _printed.FindLastIndex(l => Same(l.Label, line.Label));
        if (printed >= 0 && _printed[printed].Outcome == JournalOutcome.Waiting)
        {
            _printed[printed] = line;
            InvalidateVisual();
            return;
        }

        var queued = _queue.FindLastIndex(l => Same(l.Label, line.Label));
        if (queued >= 0 && _queue[queued].Outcome == JournalOutcome.Waiting)
        {
            _queue[queued] = line;
            return;
        }

        _queue.Add(line);

        // Строк стало больше — лента просит больше места. Без этого высота
        // останется прежней до следующей перестройки раскладки, и новая запись
        // вытеснит старую там, где место ещё было.
        InvalidateMeasure();

        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Допечатать всё разом. Человек нажал «дальше» — ждать нечего.</summary>
    public void Flush()
    {
        _printed.AddRange(_queue);
        _queue.Clear();
        _typed = 0;
        _timer.Stop();
        Trim();
        InvalidateMeasure();
        InvalidateVisual();
        Drained?.Invoke();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void Advance()
    {
        _blink++;

        if (_queue.Count == 0)
        {
            // Курсор мигает и на пустой очереди: прибор ждёт, а не завис.
            if (_blink % 16 == 0) InvalidateVisual();
            return;
        }

        if (_idle > 0)
        {
            _idle--;
            return;
        }

        var line = _queue[0];
        var length = line.Label.Length + line.Shown.Length;

        _typed += PerFrame;

        if (_typed >= length)
        {
            _printed.Add(line);
            _queue.RemoveAt(0);
            _typed = 0;
            _idle = Pause;
            Trim();

            if (_queue.Count == 0) Drained?.Invoke();
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Лента живёт весь сеанс, и держать в ней всё подряд незачем: на экране виден
    /// хвост, а прошлое некому показать. Режем с запасом, чтобы не подрезать
    /// список на каждой строке.
    /// </summary>
    private void Trim()
    {
        if (_printed.Count > 400) _printed.RemoveRange(0, 200);
    }

    private static string Mark(JournalOutcome outcome) => outcome switch
    {
        JournalOutcome.Ok => "ГОТОВ",
        JournalOutcome.Idle => "ПРОСТОЙ",
        JournalOutcome.Warning => "ВНИМАНИЕ",
        JournalOutcome.Failed => "ОТКАЗ",
        _ => "ЖДУ",
    };

    private IBrush? MarkBrush(JournalOutcome outcome) => outcome switch
    {
        JournalOutcome.Ok => Done ?? Accent,
        JournalOutcome.Idle => Muted,
        JournalOutcome.Warning => Warning,
        JournalOutcome.Failed => Error ?? Warning,
        _ => Muted,
    };

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= 1) return;

        var y = 0.0;

        // Сколько строк поместится. Отведённая высота — предел жёсткий: лента
        // стоит в колонке между другими блоками, и вылезать ей некуда.
        var fits = Math.Max(1, (int)(Bounds.Height / LineHeight));
        var room = MaxLines > 0 ? Math.Min(MaxLines, fits) : fits;

        // Печатаемая строка занимает одно место из отведённых, иначе лента, дойдя
        // до низа, прыгала бы на строку вверх при начале каждой новой записи.
        if (_queue.Count > 0) room = Math.Max(1, room - 1);

        var from = Math.Max(0, _printed.Count - room);
        var tail = 0.0;

        for (var i = from; i < _printed.Count; i++)
        {
            tail = DrawRow(context, _printed[i], y, width, int.MaxValue);
            y += LineHeight;
        }

        if (_queue.Count > 0)
        {
            var caret = DrawRow(context, _queue[0], y, width, _typed);
            DrawCaret(context, caret, y);
            return;
        }

        if (_blink % 32 >= 16 || _printed.Count == 0) return;

        // Печатать нечего — курсор ждёт в конце последней записи. Отдельной
        // строкой ниже он выглядел бы началом записи, которой нет.
        DrawCaret(context, tail, y - LineHeight);
    }

    /// <summary>
    /// Кладёт строку и возвращает, где кончился набранный текст.
    ///
    /// <para>Ответ прижат к правому краю своей колонки, а не идёт сразу за
    /// названием: так ответы всех строк стоят друг под другом и читаются
    /// столбцом. Точки-выноски заполняют промежуток — они ведут глаз и не дают
    /// прочитать ответ не от той строки.</para>
    /// </summary>
    private double DrawRow(DrawingContext context, JournalLine line, double y, double width, int typed)
    {
        var label = line.Label.ToUpperInvariant();
        var value = line.Shown.ToUpperInvariant();

        // Набираются название и ответ; выноски с меткой — не текст, а разметка
        // строки, и появляются они разом, когда ответ дописан.
        var labelTyped = typed >= label.Length ? label : label[..Math.Max(0, typed)];
        var valueTyped = typed >= label.Length
            ? value[..Math.Min(value.Length, typed - label.Length)]
            : string.Empty;

        var labelText = Layout(labelTyped, Foreground);
        context.DrawText(labelText, new Point(0, y));

        var markText = Layout(Mark(line.Outcome), MarkBrush(line.Outcome));
        var markX = width - markText.Width;

        if (typed >= label.Length + value.Length) context.DrawText(markText, new Point(markX, y));

        if (valueTyped.Length == 0) return labelText.Width;

        // Место под ответ: от конца названия до метки исхода, с просветом по обе
        // стороны. Просвет обязателен — без него длинный ответ упирался в метку и
        // читался с ней одним словом.
        var gap = TextSize * 1.6;
        var from = labelText.Width + gap;
        var room = markX - gap - from;

        // Ответ, который не помещается, режем и ставим многоточие: обрезанный
        // ответ понятен, а наехавший на метку — нет.
        value = Fit(value, room);
        if (valueTyped.Length > value.Length) valueTyped = value;

        var valueText = Layout(value, Foreground);
        var valueX = Math.Max(from, markX - gap - valueText.Width);

        var shown = Layout(valueTyped, Foreground);
        context.DrawText(shown, new Point(valueX, y));

        var dot = Layout("·", Muted);
        var dotsFrom = labelText.Width + dot.Width;
        var dotsTo = valueX - dot.Width;

        if (dotsTo <= dotsFrom || dot.Width <= 0) return valueX + shown.Width;

        // Выносок кладём целое число: обрезанная посередине точка читается
        // грязью, а не пунктиром.
        var count = (int)((dotsTo - dotsFrom) / dot.Width);
        if (count > 0) context.DrawText(Layout(new string('·', count), Muted), new Point(dotsFrom, y));

        return valueX + shown.Width;
    }

    /// <summary>Укоротить строку до заданной ширины, добавив многоточие.</summary>
    private string Fit(string text, double room)
    {
        if (room <= 0) return string.Empty;
        if (Layout(text, null).Width <= room) return text;

        for (var length = text.Length - 1; length > 1; length--)
        {
            var cut = text[..length] + "…";
            if (Layout(cut, null).Width <= room) return cut;
        }

        return "…";
    }

    private void DrawCaret(DrawingContext context, double x, double y) =>
        context.FillRectangle(
            Accent ?? Muted ?? Brushes.White,
            new Rect(x + TextSize * 0.18, y + TextSize * 0.22, TextSize * 0.5, TextSize * 0.95));

    private FormattedText Layout(string text, IBrush? brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Face, TextSize, brush ?? Brushes.White);
}
