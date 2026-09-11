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

namespace Method.App.Controls;

/// <summary>
/// Надпись, которая горит неоном, а не просто окрашена в цвет неона.
///
/// <para>Обычной надписи свечение не сделать: размытия под рукой нет, а
/// наложить текст поверх текста — значит получить утолщение, а не ореол.
/// Поэтому здесь текст переводится в геометрию и обводится: несколько широких
/// полупрозрачных обводок дают ореол, за ними тонкая — кромку, и только потом
/// сама заливка. Тот же приём, что у неонового кольца, просто контур сложнее
/// окружности.</para>
///
/// <para>Геометрия считается один раз на текст и размер и живёт в поле: её
/// построение — раскладка каждого знака по очертаниям шрифта, и делать это на
/// каждой перерисовке было бы расточительством ради надписи в одно слово.</para>
/// </summary>
public sealed class NeonText : Control
{
    /// <summary>
    /// Сколько обводок набирают ореол. Шесть, а не четыре: чем больше слоёв,
    /// тем слабее каждый и тем мягче внешняя кромка. На четырёх она читалась
    /// контуром, и свечение выглядело подложкой под словом.
    /// </summary>
    private const int Layers = 6;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<NeonText, string?>(nameof(Text));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<NeonText>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<NeonText>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<NeonText>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<NeonText>();

    /// <summary>
    /// Цвет свечения. Отдельно от цвета букв, и в этом вся суть приёма: у
    /// неоновой вывески светится воздух вокруг стекла, а сама трубка остаётся
    /// светлой. Крась букву цветом свечения — и получится не подсветка, а
    /// перекрашенная надпись, вдобавок размытая собственным ореолом.
    /// </summary>
    public static readonly StyledProperty<IBrush?> GlowBrushProperty =
        AvaloniaProperty.Register<NeonText, IBrush?>(nameof(GlowBrush));

    /// <summary>Разрядка между знаками — надпись набрана в разрядку, как на приборе.</summary>
    public static readonly StyledProperty<double> LetterSpacingProperty =
        AvaloniaProperty.Register<NeonText, double>(nameof(LetterSpacing));

    /// <summary>
    /// Насколько надпись разгорелась: от нуля до единицы. Ореол появляется
    /// вместе с цветом, а не раньше и не позже.
    /// </summary>
    public static readonly StyledProperty<double> GlowOpacityProperty =
        AvaloniaProperty.Register<NeonText, double>(nameof(GlowOpacity));

    /// <summary>Ширина ореола в точках.</summary>
    public static readonly StyledProperty<double> GlowSpreadProperty =
        AvaloniaProperty.Register<NeonText, double>(nameof(GlowSpread), 7);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? GlowBrush
    {
        get => GetValue(GlowBrushProperty);
        set => SetValue(GlowBrushProperty, value);
    }

    public double LetterSpacing
    {
        get => GetValue(LetterSpacingProperty);
        set => SetValue(LetterSpacingProperty, value);
    }

    public double GlowOpacity
    {
        get => GetValue(GlowOpacityProperty);
        set => SetValue(GlowOpacityProperty, value);
    }

    public double GlowSpread
    {
        get => GetValue(GlowSpreadProperty);
        set => SetValue(GlowSpreadProperty, value);
    }

    private FormattedText? _formatted;
    private Geometry? _outline;
    private string? _builtFor;

    static NeonText()
    {
        AffectsRender<NeonText>(
            ForegroundProperty, GlowBrushProperty, GlowOpacityProperty, GlowSpreadProperty);
        AffectsMeasure<NeonText>(
            TextProperty, FontFamilyProperty, FontSizeProperty,
            FontWeightProperty, LetterSpacingProperty);

        // Геометрия считана под прежний текст и кегль — при их смене её надо
        // построить заново, иначе на экране останется прошлая надпись.
        TextProperty.Changed.AddClassHandler<NeonText>((n, _) => n.Reset());
        FontSizeProperty.Changed.AddClassHandler<NeonText>((n, _) => n.Reset());
        FontWeightProperty.Changed.AddClassHandler<NeonText>((n, _) => n.Reset());
        FontFamilyProperty.Changed.AddClassHandler<NeonText>((n, _) => n.Reset());
        LetterSpacingProperty.Changed.AddClassHandler<NeonText>((n, _) => n.Reset());
    }

    private void Reset()
    {
        _formatted = null;
        _outline = null;
        _builtFor = null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Build();

        if (_formatted is null) return default;

        // Место под ореол с обеих сторон: без запаса широкая обводка обрезалась
        // бы границей контрола, и свечение выглядело бы срезанным ножом.
        var pad = GlowSpread;

        return new Size(_formatted.Width + pad * 2, _formatted.Height + pad * 2);
    }

    public override void Render(DrawingContext context)
    {
        Build();

        if (_formatted is null || Foreground is null) return;

        var pad = GlowSpread;
        var origin = new Point(pad, pad);

        var lit = Math.Clamp(GlowOpacity, 0, 1);
        var glow = GlowBrush ?? Foreground;

        if (lit > 0.001 && _outline is not null && glow is not null)
        {
            using var _ = context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y));

            // Снаружи внутрь: широкая и слабая обводка даёт ореол, каждая
            // следующая уже и плотнее. Обратный порядок положил бы муть поверх
            // яркого.
            //
            // Самый тонкий слой нарочно не доводится до нуля: обводка идёт по
            // контуру в обе стороны, и узкая заползала бы внутрь буквы, съедая
            // её очертания. Ровно от этого надпись выглядела размытой.
            for (var i = Layers; i >= 1; i--)
            {
                var step = (double)i / Layers;

                var thickness = GlowSpread * (0.5 + 1.8 * step);

                // Слабее, чем кажется нужным: слоёв много, и они складываются.
                var opacity = 0.085 * (1 - step) + 0.025;

                using var _layer = context.PushOpacity(opacity * lit);
                context.DrawGeometry(null, new Pen(glow, thickness, lineJoin: PenLineJoin.Round), _outline);
            }
        }

        // Буква рисуется последней и своим цветом: она остаётся резкой и
        // светлой, свечение живёт только вокруг неё.
        context.DrawText(_formatted, origin);
    }

    private void Build()
    {
        var text = Text ?? string.Empty;
        var key = $"{text}|{FontSize}|{FontWeight}|{FontFamily}|{LetterSpacing}";

        if (_builtFor == key && _formatted is not null) return;

        // Разрядка задаётся пробелами между знаками: FormattedText её не знает,
        // а надпись здесь ровно одна и короткая — разложить её по знакам дороже,
        // чем вставить проставки.
        var spaced = LetterSpacing > 0 && text.Length > 1
            ? string.Join(Gap(LetterSpacing, FontSize), text.ToCharArray())
            : text;

        _formatted = new FormattedText(
            spaced,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight),
            FontSize <= 0 ? 12 : FontSize,
            Foreground);

        _outline = _formatted.BuildGeometry(new Point(0, 0));
        _builtFor = key;
    }

    /// <summary>
    /// Чем разделять знаки. Тонкий пробел уже обычного примерно втрое, поэтому
    /// разрядка в точках пересчитывается в их количество — грубо, но для одного
    /// слова в разрядку этого довольно.
    /// </summary>
    private static string Gap(double spacing, double size) =>
        new(' ', Math.Max(1, (int)Math.Round(spacing / Math.Max(4, size * 0.28))));
}
