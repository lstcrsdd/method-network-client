// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Method.App.Controls;

/// <summary>
/// Матовое зерно поверх фона: и против полос на градиенте, и ради самой
/// поверхности — тёмное поле без фактуры выглядит пустым, с фактурой —
/// материалом.
///
/// <para>Откуда полосы. Канал цвета — восемь бит, то есть 256 ступеней. Фон
/// окна идёт от одного тёмного тона к другому, и вся разница между ними — с
/// десяток ступеней; растянутые на тысячу точек высоты развёрнутого окна, они
/// дают широкие плоские ленты с ясно видимыми границами.</para>
///
/// <para><b>Почему не простой шум.</b> Случайное число в каждой точке — это
/// белый шум, и в нём есть все частоты, включая низкие. Низкие и портят дело:
/// они собираются в облачка и пятна, поле выглядит грязным, а на длинной
/// дистанции от него рябит в глазах. Усиливать такой шум бесполезно — он
/// становится только грязнее.</para>
///
/// <para><b>Что здесь вместо него.</b> Белый шум размывается и вычитается сам
/// из себя: остаётся только то, что меняется от точки к точке, — высокие
/// частоты. Приём известный, рисунок такого рода называют синим шумом.
/// Облачков в нём нет по построению, зерно ложится ровно, и каждое зёрнышко
/// читается отдельно. Ровно этого и ждут от матовой поверхности.</para>
///
/// <para>Дальше — три условия резкости. Точка рисунка обязана совпасть с
/// точкой экрана (сторона плитки делится на масштаб окна), сглаживание при
/// укладке отключено, а отклонение идёт в обе стороны от фона: половина точек
/// светлее, половина темнее. Односторонняя плёнка не зернит, а туманит.</para>
/// </summary>
public sealed class Dither : Control
{
    /// <summary>
    /// Сторона плитки в точках экрана. Степень двойки и достаточно крупная: на
    /// мелкой плитке глаз ловит повтор рисунка, и вместо зерна получается
    /// клетчатая ткань.
    /// </summary>
    private const int Tile = 256;

    /// <summary>
    /// Наибольшая прозрачность светлой точки. Светлое зерно кладётся поверх
    /// тёмного фона и прибавляет к нему свою величину.
    /// </summary>
    private const double Light = 13;

    /// <summary>
    /// То же для тёмной точки, и заметно больше. Тёмное зерно не прибавляет, а
    /// умножает: на фоне яркостью в два десятка ступеней та же прозрачность
    /// даёт впятеро меньший сдвиг, чем у светлого.
    /// </summary>
    private const double Dark = 28;

    /// <summary>
    /// Во сколько стандартных отклонений укладывается полный размах. Три — это
    /// почти всё поле значений; что выше, обрезается, и таких точек доли
    /// процента.
    /// </summary>
    private const double Range = 3;

    /// <summary>
    /// Степень кривой, по которой отклонение подаётся на прозрачность. Меньше
    /// единицы — середина подтягивается к краям, зёрна становятся отчётливее и
    /// перестают сливаться в дымку. Ниже примерно 0,5 фактура вырождается в
    /// сплошную рябь.
    /// </summary>
    private const double Crisp = 0.55;

    private static WriteableBitmap? _noise;

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        // Масштаб окна: на нём же и держится резкость зерна. Плитку в 256 точек
        // экрана при полуторном масштабе надо уложить в 170 единиц разметки —
        // тогда точка рисунка ляжет ровно на точку экрана.
        var scaling = (this.GetVisualRoot() as ILayoutRoot)?.LayoutScaling ?? 1;

        if (scaling <= 0) scaling = 1;

        var side = Tile / scaling;

        var brush = new ImageBrush(Noise())
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, side, side, RelativeUnit.Absolute),
        };

        // Без этого укладку сгладят, и зерно снова превратится в муть.
        using var _ = context.PushRenderOptions(
            new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None });

        context.FillRectangle(brush, new Rect(Bounds.Size));
    }

    /// <summary>
    /// Плитка зерна. Одна на всё приложение: рисунок не зависит ни от размера
    /// окна, ни от оформления, а считается он дороже, чем рисуется.
    /// </summary>
    private static WriteableBitmap Noise()
    {
        if (_noise is not null) return _noise;

        var field = HighPass();

        var bitmap = new WriteableBitmap(
            new PixelSize(Tile, Tile), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var buffer = bitmap.Lock())
        {
            unsafe
            {
                var pixels = (uint*)buffer.Address;

                for (var i = 0; i < Tile * Tile; i++)
                {
                    var swing = Math.Clamp(field[i], -1, 1);

                    // Кривая резкости: знак сохраняем, силу подтягиваем вверх.
                    var shaped = Math.Sign(swing) * Math.Pow(Math.Abs(swing), Crisp);

                    pixels[i] = shaped >= 0
                        ? Lit((byte)Math.Round(shaped * Light))
                        : Shade((byte)Math.Round(-shaped * Dark));
                }
            }
        }

        _noise = bitmap;
        return _noise;
    }

    /// <summary>
    /// Поле отклонений без низких частот: белый шум минус он же размытый.
    /// Значения приведены к размаху от минус единицы до единицы по собственному
    /// разбросу поля — иначе сила зерна зависела бы от того, насколько сильно
    /// размыли.
    /// </summary>
    private static double[] HighPass()
    {
        var white = new double[Tile * Tile];
        var random = new Random(20260911);

        for (var i = 0; i < white.Length; i++)
            white[i] = random.NextDouble();

        // Одна свёртка три на три, не две: чем уже размытие, тем выше частоты
        // остаются в остатке и тем мельче выходит зерно. На двойной свёртке
        // зёрна собирались по два-три и читались уже крупинками.
        var smooth = Blur(white);

        var rest = new double[white.Length];

        for (var i = 0; i < rest.Length; i++)
            rest[i] = white[i] - smooth[i];

        var mean = 0.0;

        foreach (var v in rest) mean += v;

        mean /= rest.Length;

        var spread = 0.0;

        foreach (var v in rest) spread += (v - mean) * (v - mean);

        spread = Math.Sqrt(spread / rest.Length);

        if (spread <= 0) return rest;

        for (var i = 0; i < rest.Length; i++)
            rest[i] = (rest[i] - mean) / (spread * Range);

        return rest;
    }

    /// <summary>
    /// Размытие три на три с заворотом по краям. Заворот обязателен: плитка
    /// укладывается встык сама с собой, и без него по швам пошла бы сетка.
    /// </summary>
    private static double[] Blur(double[] source)
    {
        var result = new double[source.Length];

        for (var y = 0; y < Tile; y++)
        {
            for (var x = 0; x < Tile; x++)
            {
                var sum = 0.0;

                for (var dy = -1; dy <= 1; dy++)
                {
                    var row = ((y + dy) % Tile + Tile) % Tile;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var col = ((x + dx) % Tile + Tile) % Tile;

                        sum += source[row * Tile + col];
                    }
                }

                result[y * Tile + x] = sum / 9;
            }
        }

        return result;
    }

    /// <summary>
    /// Светлая точка. Цвет хранится помноженным на прозрачность, поэтому все
    /// каналы равны ей: это и есть полупрозрачный белый.
    /// </summary>
    private static uint Lit(byte alpha) =>
        (uint)(alpha << 24 | alpha << 16 | alpha << 8 | alpha);

    /// <summary>Тёмная точка: цвет нулевой, от прозрачности остаётся только она.</summary>
    private static uint Shade(byte alpha) => (uint)alpha << 24;
}
