// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Method.App.Views.Pages;

/// <summary>
/// Обзор в основном оформлении. Содержимое — в разметке, отсюда задаётся только
/// масштаб композиции.
///
/// <para>В обычном окне размеры выверены и трогать их нельзя, а на развёрнутом
/// та же композиция занимает середину огромного поля и выглядит потерянной.
/// Поэтому она растёт вместе с окном — целиком, одним преобразованием, а не
/// перебором размеров и кеглей по одному: второй набор чисел разошёлся бы с
/// первым на первой же правке.</para>
///
/// <para>Рост непрерывный, а не ступенькой. Ступенька была первой попыткой:
/// развёрнуто — полтора, иначе — единица. На словах верно, на деле половина
/// размеров окна лежит между этими двумя состояниями, и при переходе туда
/// композиция не менялась вовсе, а у границы прыгала рывком.</para>
/// </summary>
public partial class OverviewPlain : UserControl
{
    /// <summary>
    /// Ширина страницы, до которой композиция остаётся в исходном размере. Это
    /// примерно окно, открытое по умолчанию, растянутое на треть шире: до сих
    /// пор раскладка выверена и увеличивать нечего.
    /// </summary>
    private const double Plain = 1400;

    /// <summary>
    /// Насколько крупнее композиция может стать. Полтора — столько же, сколько
    /// давала прежняя ступенька на развёрнутом окне.
    /// </summary>
    private const double Most = 1.5;

    /// <summary>Поля прокрутки по вертикали — они в масштаб не входят.</summary>
    private const double ScrollInset = 34;

    private readonly LayoutTransformControl _zoom;
    private readonly Grid _composition;

    public OverviewPlain()
    {
        AvaloniaXamlLoader.Load(this);

        _zoom = this.FindControl<LayoutTransformControl>("Zoom")!;
        _composition = this.FindControl<Grid>("Composition")!;

        SizeChanged += (_, e) => Apply(e.NewSize);
    }

    /// <summary>
    /// Раскладку пересчитываем и при появлении страницы, а не только по событию
    /// размера. Скрытая страница в раскладке не участвует, и смену размера окна,
    /// случившуюся в другом разделе, она пропускает целиком: вернёшься в обзор —
    /// а там масштаб от прошлого размера окна.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Apply(Bounds.Size);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>()) Apply(Bounds.Size);
    }

    private void Apply(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;

        // Растём пропорционально ширине и упираемся в потолок. Снизу единица:
        // уменьшать выверенную раскладку незачем, да и некуда — при узком окне
        // она и так впритык.
        var scale = Math.Clamp(size.Width / Plain, 1, Most);

        if (_zoom.LayoutTransform is not ScaleTransform current ||
            Math.Abs(current.ScaleX - scale) > 0.001)
        {
            _zoom.LayoutTransform = new ScaleTransform(scale, scale);
        }

        // Наименьшая высота содержимого — видимая область, делённая на масштаб.
        // Без деления увеличенная страница выталкивает сама себя за нижний край
        // и заводит прокрутку на ровном месте; без самой высоты растяжки внутри
        // схлопнутся в ноль, и кнопка перестанет стоять по центру.
        _composition.MinHeight = Math.Max(0, size.Height - ScrollInset) / scale;
    }
}
