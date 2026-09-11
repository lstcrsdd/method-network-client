// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Method.App.Views.Pages;

/// <summary>
/// Обзор в основном оформлении. Содержимое — в разметке; отсюда задаётся только
/// масштаб композиции.
///
/// <para>В обычном окне размеры выверены и трогать их нельзя. На развёрнутом же
/// та же композиция занимает середину огромного поля и выглядит потерянной,
/// поэтому там она берётся в полтора раза крупнее — целиком, одним
/// преобразованием, а не перебором размеров и кеглей по одному. Второй набор
/// чисел разошёлся бы с первым на первой же правке.</para>
/// </summary>
public partial class OverviewPlain : UserControl
{
    /// <summary>
    /// С какой ширины страница считается просторной. Порог тот же, что у
    /// плакатного обзора: на 1180, с каким окно открывается, и на любом
    /// разумном увеличении мышью он не срабатывает.
    /// </summary>
    private const double Spacious = 1500;

    /// <summary>Во сколько раз крупнее на развёрнутом окне.</summary>
    private const double Zoomed = 1.5;

    /// <summary>Поля прокрутки по вертикали — они в масштаб не входят.</summary>
    private const double ScrollInset = 34;

    public OverviewPlain()
    {
        AvaloniaXamlLoader.Load(this);

        var zoom = this.FindControl<LayoutTransformControl>("Zoom")!;
        var composition = this.FindControl<Grid>("Composition")!;

        SizeChanged += (_, e) =>
        {
            var scale = e.NewSize.Width >= Spacious ? Zoomed : 1;

            // Преобразование пересоздаём только при смене масштаба: у коробки от
            // этого пересчёт раскладки, а событие приходит на каждый пиксель
            // перетаскивания края окна.
            if (zoom.LayoutTransform is not ScaleTransform current ||
                Math.Abs(current.ScaleX - scale) > 0.001)
            {
                zoom.LayoutTransform = new ScaleTransform(scale, scale);
            }

            // Наименьшая высота содержимого — видимая область, делённая на
            // масштаб. Без деления увеличенная страница выталкивает сама себя за
            // нижний край и заводит прокрутку на ровном месте; без самой высоты
            // растяжки внутри схлопнутся в ноль, и кнопка перестанет стоять по
            // центру.
            composition.MinHeight = Math.Max(0, e.NewSize.Height - ScrollInset) / scale;
        };
    }
}
