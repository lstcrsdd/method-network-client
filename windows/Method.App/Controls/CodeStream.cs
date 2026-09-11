// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Method.App.Controls;

/// <summary>
/// Бегущая лента исходного кода — та самая «голограмма» под сводкой.
///
/// <para>Украшение, и это сказано честно: работе клиента она ничего не
/// добавляет и появляется только там, где на экране всё равно пустует место.
/// Но код в ней настоящий, вшитый в саму сборку: клиент открытый, и набор
/// правдоподобных знаков «под хакера» был бы единственным враньём на экране,
/// где всё остальное — правда.</para>
///
/// <para>Рисуется дёшево. Разметка строки считается один раз и живёт дальше в
/// кэше, а на кадре чертятся только те строки, что попали в окно, — десяток из
/// тысячи. Иначе лента, которая существует ради красоты, съедала бы процессор
/// у того, ради чего клиент и запущен.</para>
/// </summary>
public sealed class CodeStream : Control
{
    /// <summary>Пикселей в секунду. Медленно: лента должна течь, а не мелькать.</summary>
    private const double Speed = 16;

    /// <summary>
    /// Кадров в секунду. Четырнадцати хватает: на такой скорости кадр сдвигает
    /// текст на пиксель с небольшим, и глаз разницы с двадцатью не видит, а
    /// процессора уходит вдвое меньше. Украшение не имеет права стоить дорого.
    /// </summary>
    private const int Fps = 14;

    public static readonly StyledProperty<double> TextSizeProperty =
        AvaloniaProperty.Register<CodeStream, double>(nameof(TextSize), 10);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<CodeStream, IBrush?>(nameof(Foreground));

    /// <summary>Размер знаков.</summary>
    public double TextSize
    {
        get => GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    /// <summary>Цвет строк. Яркость гасится маской, поэтому кисть берётся как есть.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private static readonly Typeface Face = new(new FontFamily("Cascadia Mono, Consolas, Courier New"));

    private static string[]? _lines;

    private readonly Dictionary<int, FormattedText> _cache = [];
    private DispatcherTimer? _timer;
    private double _offset;

    static CodeStream()
    {
        AffectsRender<CodeStream>(TextSizeProperty, ForegroundProperty);

        // Разметка строк считана под прежний размер и цвет — при их смене кэш
        // надо выбросить, иначе лента останется прежней навсегда.
        TextSizeProperty.Changed.AddClassHandler<CodeStream>((c, _) => c._cache.Clear());
        ForegroundProperty.Changed.AddClassHandler<CodeStream>((c, _) => c._cache.Clear());
    }

    private double LineHeight => Math.Round(TextSize * 1.55);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // Разметка строк посчитана под прежнюю ширину: сменилась она — кэш
        // устарел целиком, иначе строки останутся обрезанными по старому краю.
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) _cache.Clear();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / Fps) };
        _timer.Tick += Step;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Таймер снимается вместе с контролом. Оставленный, он крутился бы
        // дальше на странице, которую никто не смотрит.
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Step;
            _timer = null;
        }

        _cache.Clear();
    }

    private void Step(object? sender, EventArgs e)
    {
        // Скрытая лента не двигается. В обычном окне её прячут целиком, но из
        // дерева она при этом не уходит: считать кадры и звать перерисовку для
        // того, чего никто не видит, — чистая растрата.
        if (!IsEffectivelyVisible) return;

        var lines = Lines();
        if (lines.Length == 0) return;

        _offset += Speed / Fps;

        // Прокрутка зациклена по всей длине: домотав до конца, лента начинает
        // сначала, и шва не видно — строки и так идут не по порядку.
        var total = lines.Length * LineHeight;
        if (total > 0 && _offset >= total) _offset -= total;

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var lines = Lines();
        if (lines.Length == 0 || Bounds.Height <= 0 || Foreground is null) return;

        var step = LineHeight;
        var first = (int)Math.Floor(_offset / step);
        var shift = _offset - first * step;

        var visible = (int)Math.Ceiling(Bounds.Height / step) + 1;

        for (var i = 0; i < visible; i++)
        {
            var index = (first + i) % lines.Length;
            var y = i * step - shift;

            var text = Formatted(index, lines[index]);
            context.DrawText(text, new Point(0, y));
        }
    }

    /// <summary>
    /// Разметка строки. Считается один раз: построение FormattedText — это
    /// разбор шрифта и раскладка знаков, и делать его двадцать раз в секунду на
    /// каждую видимую строку означало бы платить процессором за украшение.
    /// </summary>
    private FormattedText Formatted(int index, string line)
    {
        if (_cache.TryGetValue(index, out var cached)) return cached;

        var text = new FormattedText(
            line,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Face,
            TextSize,
            Foreground)
        {
            // Строка обрезается по ширине ленты, а не по числу знаков. Знаков
            // ровно столько, сколько влезает: обрезание вслепую то не доводит
            // строку до края, то выпускает её за него, и правый край ленты
            // получается рваным вместо ровного.
            MaxTextWidth = Math.Max(1, Bounds.Width),

            // Одна строка на запись: перенос сбил бы построчный шаг, на котором
            // держится вся прокрутка.
            MaxTextHeight = Math.Max(1, LineHeight),
        };

        _cache[index] = text;
        return text;
    }

    /// <summary>
    /// Строки кода из вшитых исходников.
    ///
    /// <para>Читаются один раз на всё приложение: файлы лежат в сборке и не
    /// меняются, а перечитывать их при каждом открытии раздела — это разбор
    /// сотни килобайт ради картинки.</para>
    ///
    /// <para>Комментарии отброшены, и это не потеря. В самих файлах они главное
    /// — тем и ценны, — но бегущая лента читается не как исходник, а как
    /// картинка: строка русского текста в ней выглядит попавшей туда по
    /// ошибке, а весь эффект держится на том, что мимо идёт работающий код.</para>
    ///
    /// <para>Пустые строки и одинокие закрывающие скобки выброшены: в бегущей
    /// ленте они читаются провалами, будто текст порвался. Отступы срезаны по
    /// той же причине — лента узкая, и строка, начинающаяся с восьми пробелов,
    /// показывает только пробелы.</para>
    /// </summary>
    private static string[] Lines()
    {
        if (_lines is not null) return _lines;

        var assembly = Assembly.GetExecutingAssembly();
        var collected = new List<string>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("source/", StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();

                if (trimmed.Length < 3) continue;
                if (trimmed is "{" or "}" or "};") continue;

                // Комментарии — мимо: строчные, документирующие и продолжения
                // блочных. Мимо идёт работающий код, на этом всё и держится.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("/*", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                // Очень длинные строки обрезаем: перенос в ленте сбил бы
                // построчный шаг, на котором держится вся прокрутка.
                collected.Add(trimmed.Length > 96 ? trimmed[..96] : trimmed);
            }
        }

        _lines = collected.ToArray();
        return _lines;
    }
}
