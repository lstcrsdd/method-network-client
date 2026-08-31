// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Method.App.ViewModels;

namespace Method.App.Views.Pages;

public partial class ScenariosPage : UserControl
{
    public ScenariosPage() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Выбрать программу через обычный диалог открытия файла.
    ///
    /// <para>В поле кладём имя файла, а не полный путь: программы обновляются и
    /// переезжают между папками с номером версии, а имя остаётся. Кому нужен
    /// именно путь — вписывает его руками, разбор различает эти случаи сам.</para>
    /// </summary>
    private async void OnPickProgram(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (TopLevel.GetTopLevel(this) is not { StorageProvider: { } storage }) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите программу",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Программы") { Patterns = ["*.exe"] }],
        });

        if (files.Count == 0) return;

        model.NewRuleValue = files[0].Name;
        model.RuleError = null;
    }
}
