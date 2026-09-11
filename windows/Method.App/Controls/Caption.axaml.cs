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
/// Надпись в двух регистрах сразу: обычном и прописном. Какой из них показать,
/// решает оформление — то же устройство, что у названия раздела, только без
/// номера и линейки.
/// </summary>
public partial class Caption : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Caption, string?>(nameof(Text));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Caption() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != TextProperty) return;

        var text = Text ?? string.Empty;
        this.FindControl<TextBlock>("PlainCaptionText")!.Text = text;
        this.FindControl<TextBlock>("CapsCaptionText")!.Text = text.ToUpperInvariant();
    }
}
