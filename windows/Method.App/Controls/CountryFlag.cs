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
/// Флаг страны.
///
/// <para><b>Почему рисуем сами, а не берём эмодзи.</b> На Windows флагов в
/// шрифте нет вовсе: пара региональных указателей показывается двумя буквами в
/// квадратиках. То, что на macOS выглядит флагом, здесь выглядит поломкой.</para>
///
/// <para><b>Почему полосами, а не картинками.</b> Набор картинок пришлось бы
/// возить в поставке и всё равно упереться в страну, которой в нём нет: узлы
/// бывают где угодно. Полосы описываются тремя цветами и покрывают большинство
/// флагов, а для остальных остаётся честный запасной вид — код страны.</para>
///
/// <para>Флаги со сложным рисунком — с гербами, звёздами, полумесяцами — мы не
/// подделываем: узнаваемо нарисовать их полосами нельзя, а плохо нарисованный
/// флаг хуже его отсутствия.</para>
/// </summary>
public sealed class CountryFlag : Control
{
    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<CountryFlag, string?>(nameof(Code));

    public static readonly StyledProperty<IBrush?> FallbackForegroundProperty =
        AvaloniaProperty.Register<CountryFlag, IBrush?>(nameof(FallbackForeground));

    public string? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    /// <summary>Цвет кода страны, когда рисовать нечего.</summary>
    public IBrush? FallbackForeground
    {
        get => GetValue(FallbackForegroundProperty);
        set => SetValue(FallbackForegroundProperty, value);
    }

    static CountryFlag() => AffectsRender<CountryFlag>(CodeProperty, FallbackForegroundProperty);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        var rect = new Rect(0, 0, width, height);
        var code = Code?.ToUpperInvariant();

        if (code is null || !Flags.TryGetValue(code, out var flag))
        {
            DrawFallback(context, rect, code);
            return;
        }

        using (context.PushClip(new RoundedRect(rect, 3)))
        {
            switch (flag.Kind)
            {
                case FlagKind.Horizontal:
                    DrawBands(context, rect, flag.Colors, horizontal: true);
                    break;
                case FlagKind.Vertical:
                    DrawBands(context, rect, flag.Colors, horizontal: false);
                    break;
                case FlagKind.NordicCross:
                    DrawNordicCross(context, rect, flag.Colors);
                    break;
                case FlagKind.Stars:
                    DrawStarsAndStripes(context, rect, flag.Colors);
                    break;
            }
        }

        // Тонкая обводка: светлые флаги иначе сливаются с карточкой.
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.White, 0.18), 1), new RoundedRect(rect, 3));
    }

    private void DrawFallback(DrawingContext context, Rect rect, string? code)
    {
        var brush = FallbackForeground ?? Brushes.Gray;

        context.DrawRectangle(
            new SolidColorBrush(Colors.White, 0.08),
            new Pen(new SolidColorBrush(Colors.White, 0.18), 1),
            new RoundedRect(rect, 3));

        if (string.IsNullOrEmpty(code)) return;

        var text = new FormattedText(
            code,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            rect.Height * 0.5,
            brush);

        context.DrawText(text, new Point(
            rect.X + (rect.Width - text.Width) / 2,
            rect.Y + (rect.Height - text.Height) / 2));
    }

    private static void DrawBands(DrawingContext context, Rect rect, string[] colors, bool horizontal)
    {
        var count = colors.Length;

        for (var i = 0; i < count; i++)
        {
            var brush = new SolidColorBrush(Color.Parse(colors[i]));

            var band = horizontal
                ? new Rect(rect.X, rect.Y + rect.Height * i / count, rect.Width, rect.Height / count + 0.5)
                : new Rect(rect.X + rect.Width * i / count, rect.Y, rect.Width / count + 0.5, rect.Height);

            context.FillRectangle(brush, band);
        }
    }

    /// <summary>Скандинавский крест: фон, затем полосы креста со сдвигом влево.</summary>
    private static void DrawNordicCross(DrawingContext context, Rect rect, string[] colors)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse(colors[0])), rect);

        var cross = new SolidColorBrush(Color.Parse(colors[1]));
        var thickness = rect.Height * 0.22;
        var offset = rect.Width * 0.36;

        context.FillRectangle(cross, new Rect(rect.X, rect.Y + (rect.Height - thickness) / 2, rect.Width, thickness));
        context.FillRectangle(cross, new Rect(rect.X + offset - thickness / 2, rect.Y, thickness, rect.Height));
    }

    /// <summary>Полосы и синий угол. Звёзды на таком размере не читаются — и не нужны.</summary>
    private static void DrawStarsAndStripes(DrawingContext context, Rect rect, string[] colors)
    {
        var stripe = new SolidColorBrush(Color.Parse(colors[0]));
        var white = new SolidColorBrush(Color.Parse(colors[1]));
        var canton = new SolidColorBrush(Color.Parse(colors[2]));

        context.FillRectangle(white, rect);

        const int stripes = 7;
        for (var i = 0; i < stripes; i++)
        {
            var y = rect.Y + rect.Height * (i * 2) / 13.0;
            context.FillRectangle(stripe, new Rect(rect.X, y, rect.Width, rect.Height / 13.0 + 0.5));
        }

        context.FillRectangle(canton, new Rect(rect.X, rect.Y, rect.Width * 0.42, rect.Height * 7 / 13.0));
    }

    private enum FlagKind
    {
        Horizontal,
        Vertical,
        NordicCross,
        Stars,
    }

    private sealed record Flag(FlagKind Kind, params string[] Colors);

    /// <summary>
    /// Флаги, которые честно раскладываются на полосы. Остальные показываются
    /// кодом страны: подделка узнаваемости не добавит.
    /// </summary>
    private static readonly Dictionary<string, Flag> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AT"] = new(FlagKind.Horizontal, "#ED2939", "#FFFFFF", "#ED2939"),
        ["BE"] = new(FlagKind.Vertical, "#000000", "#FDDA24", "#EF3340"),
        ["BG"] = new(FlagKind.Horizontal, "#FFFFFF", "#00966E", "#D62612"),
        ["CZ"] = new(FlagKind.Horizontal, "#FFFFFF", "#D7141A"),
        ["DE"] = new(FlagKind.Horizontal, "#000000", "#DD0000", "#FFCE00"),
        ["DK"] = new(FlagKind.NordicCross, "#C60C30", "#FFFFFF"),
        ["EE"] = new(FlagKind.Horizontal, "#0072CE", "#000000", "#FFFFFF"),
        ["FI"] = new(FlagKind.NordicCross, "#FFFFFF", "#003580"),
        ["FR"] = new(FlagKind.Vertical, "#002395", "#FFFFFF", "#ED2939"),
        ["HU"] = new(FlagKind.Horizontal, "#CE2939", "#FFFFFF", "#477050"),
        ["IE"] = new(FlagKind.Vertical, "#169B62", "#FFFFFF", "#FF883E"),
        ["IS"] = new(FlagKind.NordicCross, "#02529C", "#DC1E35"),
        ["IT"] = new(FlagKind.Vertical, "#008C45", "#F4F5F0", "#CD212A"),
        ["LT"] = new(FlagKind.Horizontal, "#FDB913", "#006A44", "#C1272D"),
        ["LU"] = new(FlagKind.Horizontal, "#ED2939", "#FFFFFF", "#00A1DE"),
        ["LV"] = new(FlagKind.Horizontal, "#9E3039", "#FFFFFF", "#9E3039"),
        ["NL"] = new(FlagKind.Horizontal, "#AE1C28", "#FFFFFF", "#21468B"),
        ["NO"] = new(FlagKind.NordicCross, "#BA0C2F", "#00205B"),
        ["PL"] = new(FlagKind.Horizontal, "#FFFFFF", "#DC143C"),
        ["RO"] = new(FlagKind.Vertical, "#002B7F", "#FCD116", "#CE1126"),
        ["RS"] = new(FlagKind.Horizontal, "#C6363C", "#0C4076", "#FFFFFF"),
        ["RU"] = new(FlagKind.Horizontal, "#FFFFFF", "#0039A6", "#D52B1E"),
        ["SE"] = new(FlagKind.NordicCross, "#006AA7", "#FECC02"),
        ["UA"] = new(FlagKind.Horizontal, "#005BBB", "#FFD500"),
        ["US"] = new(FlagKind.Stars, "#B22234", "#FFFFFF", "#3C3B6E"),
    };
}
