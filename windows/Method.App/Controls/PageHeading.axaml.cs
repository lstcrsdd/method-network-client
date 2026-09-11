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

namespace Method.App.Controls;

/// <summary>
/// Название раздела в двух видах сразу: обычном и плакатном.
///
/// <para>Прописные буквы считаются здесь, а не пишутся в разметке страницы:
/// название раздела должно быть написано один раз и по-человечески, иначе при
/// правке текста две записи разъезжаются — а разъехавшись, они этого никак не
/// показывают.</para>
/// </summary>
public partial class PageHeading : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<PageHeading, string?>(nameof(Text));

    /// <summary>Порядковый номер раздела. Виден только в плакатном оформлении.</summary>
    public static readonly StyledProperty<string?> NumberProperty =
        AvaloniaProperty.Register<PageHeading, string?>(nameof(Number));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Number
    {
        get => GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    public PageHeading() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            var text = Text ?? string.Empty;
            this.FindControl<TextBlock>("PlainTitleText")!.Text = text;

            // Разрядку задаёт оформление, а вот прописные — забота текста:
            // в разметке название пишется один раз и в обычном виде.
            this.FindControl<TextBlock>("CapsText")!.Text = text.ToUpperInvariant();
        }
        else if (change.Property == NumberProperty)
        {
            this.FindControl<TextBlock>("NumberText")!.Text = Number ?? string.Empty;
        }
    }
}
