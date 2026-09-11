// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Media;

namespace Method.App.Controls;

/// <summary>
/// Общие правила неона: кольцо вокруг кнопки, знак внутри неё и точка
/// состояния светятся по одному рецепту, и рецепт лежит здесь, а не в трёх
/// местах сразу.
///
/// <para>Рецепт короткий. Свечение набирается слоями одного цвета — от широкого
/// и почти прозрачного снаружи к узкому и плотному внутри. Последним идёт
/// сердцевина: тот же цвет, уведённый к белому. Чистый белый сюда не годится —
/// он читается отдельной белой чертой поверх цветной обводки, а не разрядом
/// внутри трубки.</para>
/// </summary>
internal static class Neon
{
    /// <summary>
    /// Насколько сердцевина светлее собственного цвета. Не до белого: у
    /// газоразрядной трубки середина выбелена, но цвет в ней угадывается.
    /// </summary>
    private const double Lift = 0.65;

    /// <summary>
    /// Прозрачность слоя свечения. Самый внутренний — сплошной, он и есть
    /// нить; остальные падают быстрее, чем растёт их ширина, иначе ореол
    /// читается вторым контуром, а не светом.
    /// </summary>
    public static double LayerOpacity(int layer, double step) =>
        layer == 1 ? 1 : 0.1 * (1 - step) + 0.03;

    /// <summary>
    /// Цвет сердцевины — тот же, уведённый к белому. Берётся из кисти, а не
    /// задаётся отдельно: цвет свечения приходит из набора оформления, и второй
    /// ключ рядом с ним пришлось бы держать в согласии руками.
    /// </summary>
    public static IBrush? Filament(IBrush? glow)
    {
        if (glow is not ISolidColorBrush solid) return null;

        var colour = solid.Color;

        return new SolidColorBrush(Color.FromArgb(
            colour.A,
            Whiten(colour.R),
            Whiten(colour.G),
            Whiten(colour.B)));

        static byte Whiten(byte channel) => (byte)(channel + (255 - channel) * Lift);
    }
}
