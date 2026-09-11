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
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Method.App.Controls;

/// <summary>
/// Ламповый экран: то, что внутри, показывается строками развёртки, с медленно
/// ползущей полосой кадровой рассинхронизации и лёгкой прозрачностью проекции.
///
/// <para>Работает маской, а не накладкой поверх. Разница принципиальная: накладка
/// красит прямоугольник целиком, и полосы шли бы по пустому полю вокруг знака —
/// экраном становилось бы всё окно. Маска действует только там, где что-то
/// нарисовано, поэтому строки ложатся на сам знак, на знамёна и лучи, а фон
/// остаётся чистым.</para>
///
/// <para>Маска — узкая картинка в один пиксель шириной, растянутая по ширине
/// поля: развёртка идёт строками, и по горизонтали в ней ничего не меняется.
/// Перерисовывается она по таймеру, тридцати кадров в секунду хватает — полоса
/// ползёт неспешно, около семи секунд на проход. Быстрая полоса читается как
/// мигание и надоедает через минуту; медленная — как работающий прибор.</para>
/// </summary>
public sealed class TubeScreen : Decorator
{
    private const double Fps = 30;

    /// <summary>Шаг строк развёртки в точках.</summary>
    public static readonly StyledProperty<double> PitchProperty =
        AvaloniaProperty.Register<TubeScreen, double>(nameof(Pitch), 3);

    /// <summary>Насколько гасит строка развёртки.</summary>
    public static readonly StyledProperty<double> LineDepthProperty =
        AvaloniaProperty.Register<TubeScreen, double>(nameof(LineDepth), 0.10);

    /// <summary>Общая прозрачность проекции: изображение, а не наклейка.</summary>
    public static readonly StyledProperty<double> HazeProperty =
        AvaloniaProperty.Register<TubeScreen, double>(nameof(Haze), 0.07);

    /// <summary>Сколько секунд полоса идёт сверху донизу.</summary>
    public static readonly StyledProperty<double> RollSecondsProperty =
        AvaloniaProperty.Register<TubeScreen, double>(nameof(RollSeconds), 7);

    /// <summary>
    /// Высота полосы — доля высоты поля. Ноль означает «без полосы»: остаются
    /// строки развёртки и дыхание яркости, а ползущего блика нет.
    ///
    /// <para>Нужно это там, где под стеклом лежит текст. Полоса задумана как
    /// кадровая рассинхронизация — она уместна на рисунке, который смотрят
    /// целиком, но по бегущим строкам она проходит вторым движением поперёк
    /// первого, и читать становится нечего.</para>
    /// </summary>
    public static readonly StyledProperty<double> BandShareProperty =
        AvaloniaProperty.Register<TubeScreen, double>(nameof(BandShare), 0.26);

    public double Pitch
    {
        get => GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    public double LineDepth
    {
        get => GetValue(LineDepthProperty);
        set => SetValue(LineDepthProperty, value);
    }

    public double Haze
    {
        get => GetValue(HazeProperty);
        set => SetValue(HazeProperty, value);
    }

    public double RollSeconds
    {
        get => GetValue(RollSecondsProperty);
        set => SetValue(RollSecondsProperty, value);
    }

    public double BandShare
    {
        get => GetValue(BandShareProperty);
        set => SetValue(BandShareProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _mask;
    private int _height;
    private double _phase;
    private double _breath;

    public TubeScreen()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1 / Fps) };
        _timer.Tick += (_, _) => Step();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _timer.Stop();
        OpacityMask = null;
        _mask?.Dispose();
        _mask = null;
        _height = 0;
    }

    private void Step()
    {
        var height = (int)Math.Round(Bounds.Height);
        if (height < 8) return;

        // Фазу двигаем по времени кадра, а не по их числу: пропущенный кадр не
        // должен замедлять полосу, иначе на загруженной машине она ползёт
        // медленнее, чем на свободной.
        _phase += 1 / (Fps * Math.Max(1, RollSeconds));
        if (_phase > 1) _phase -= 1;

        // Дыхание яркости: у лампового экрана она никогда не стоит ровно.
        _breath += 1 / (Fps * 3.5);
        if (_breath > 1) _breath -= 1;

        Paint(height);
    }

    /// <summary>
    /// Перерисовать маску. Картинка шириной в один пиксель: по горизонтали в
    /// развёртке ничего не меняется, а растянет её кисть.
    /// </summary>
    private void Paint(int height)
    {
        if (_mask is null || _height != height)
        {
            _mask?.Dispose();
            _mask = new WriteableBitmap(
                new PixelSize(1, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            _height = height;
        }

        var pitch = Math.Max(2, Pitch);
        var band = BandShare <= 0 ? 0 : height * Math.Clamp(BandShare, 0.05, 0.9);

        // Полоса выходит за нижний край и втекает сверху: у настоящей развёртки
        // низ и верх кадра связаны, полоса не исчезает и не появляется заново.
        var centre = _phase * (height + band) - band / 2;
        var breath = 1 - Haze * 0.25 * (1 + Math.Sin(_breath * Math.PI * 2)) / 2;

        using var buffer = _mask.Lock();

        unsafe
        {
            var pixels = (uint*)buffer.Address;

            for (var y = 0; y < height; y++)
            {
                var alpha = (1 - Haze) * breath;

                // Строки развёртки. Гасим каждую вторую полосу шага, а не одну
                // тонкую линию: одиночная линия на тонком рисунке пропадает,
                // а пара «светлая-тёмная» читается фактурой.
                if (y % pitch < 1) alpha *= 1 - LineDepth;

                // Полоса: впереди тёмная кромка, за ней просветление. Именно
                // такой порядок был у кадровой рассинхронизации, и без кромки
                // полоса читается пятном, а не движением.
                if (band > 0)
                {
                    var offset = (y - centre) / (band / 2);

                    if (Math.Abs(offset) < 1)
                    {
                        var shape = Math.Cos(offset * Math.PI / 2);

                        if (offset < -0.55) alpha *= 1 - 0.22 * shape;
                        else alpha *= 1 + 0.12 * shape;
                    }
                }

                var value = (byte)Math.Clamp(alpha * 255, 0, 255);

                // Маска берётся по альфе, но цвет хранится помноженным на неё:
                // серый здесь — это и есть частичная прозрачность.
                pixels[y] = (uint)(value << 24 | value << 16 | value << 8 | value);
            }
        }

        // Кисть пересоздаём: содержимое картинки поменялось, а на это одно
        // обновление слоя не назначается — старая кисть так и осталась бы.
        OpacityMask = new ImageBrush
        {
            Source = _mask,
            Stretch = Stretch.Fill,
        };
    }
}
