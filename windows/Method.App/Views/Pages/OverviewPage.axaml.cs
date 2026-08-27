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

namespace Method.App.Views.Pages;

public partial class OverviewPage : UserControl
{
    /// <summary>
    /// Выше этой высоты страницы композиция берёт крупные размеры. Порог взят
    /// между рабочей высотой обычного окна и высотой развёрнутого.
    /// </summary>
    private const double RoomyHeight = 850;

    /// <summary>Части композиции, которым положено расти вместе.</summary>
    private static readonly string[] StageParts =
        ["Stage", "ConnectButton", "StageEmblem", "StageRays", "StageBanners", "ActionCaption"];

    private StyledElement[]? _parts;
    private bool _roomy;

    public OverviewPage() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Метку «просторно» вешаем на каждую часть композиции отдельно.
    ///
    /// <para>Напрашивалось пометить окно и написать стили вида
    /// <c>Window.roomy Panel.dialArea</c>, но так не работает: Avalonia не
    /// пересчитывает селекторы с предком, когда у предка меняются классы, —
    /// класс на окне выставлялся исправно, а размеры оставались прежними.
    /// Метка на самом элементе отслеживается всегда.</para>
    ///
    /// <para>Смотрим на высоту страницы, а не на состояние окна: развёрнутое
    /// снаружи окно своего состояния приложению не сообщает, да и растянутому
    /// вручную окну место нужно ровно так же.</para>
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != BoundsProperty) return;

        var roomy = Bounds.Height >= RoomyHeight;
        if (_parts is not null && roomy == _roomy) return;

        _roomy = roomy;
        foreach (var part in Parts()) part.Classes.Set("roomy", roomy);
    }

    /// <summary>
    /// Элементы ищем по имени, а не через поля разметки: поля заполняет
    /// <c>InitializeComponent</c>, а страница грузит разметку напрямую — с
    /// полями здесь всё время был null, и метка никуда не доезжала.
    /// </summary>
    private StyledElement[] Parts()
    {
        if (_parts is not null) return _parts;

        var found = new List<StyledElement>(StageParts.Length);

        foreach (var name in StageParts)
            if (this.FindControl<Control>(name) is { } control)
                found.Add(control);

        // Кэшируем только когда нашлось всё: первые измерения приходят раньше,
        // чем дерево собрано целиком.
        var parts = found.ToArray();
        if (parts.Length == StageParts.Length) _parts = parts;

        return parts;
    }
}
