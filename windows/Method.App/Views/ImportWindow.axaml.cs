// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Method.App.ViewModels;

namespace Method.App.Views;

public partial class ImportWindow : Window
{
    private readonly MainViewModel _model;

    // Нужен сборщику превью Avalonia; в работе не вызывается.
    public ImportWindow() : this(new MainViewModel())
    {
    }

    public ImportWindow(MainViewModel model)
    {
        _model = model;
        AvaloniaXamlLoader.Load(this);
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<TextBox>("LinksBox");
        var summary = this.FindControl<TextBlock>("Summary");
        var button = sender as Button;
        if (box is null || summary is null) return;

        var text = box.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            Show(summary, "Вставьте хотя бы одну ссылку или адрес подписки.");
            return;
        }

        // Загрузка подписки идёт по сети и занимает секунды. Без запрета повторного
        // нажатия человек нажмёт ещё раз, и подписка добавится дважды.
        if (button is not null) button.IsEnabled = false;
        Show(summary, "Загружаю…");

        ImportResult result;
        try
        {
            result = await _model.AddFromTextAsync(text);
        }
        catch (Exception ex)
        {
            Show(summary, $"Не вышло: {ex.Message}");
            return;
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }

        // Закрываем только когда рассказывать нечего: добавилось всё, что было.
        // Иначе окно остаётся открытым вместе с текстом — человек видит, что именно
        // не подошло, и правит на месте.
        if (result.Errors.Count == 0 && result.SubscriptionErrors.Count == 0
            && result.Duplicates == 0 && result.Added > 0)
        {
            Close();
            return;
        }

        Show(summary, Describe(result));
    }

    private static void Show(TextBlock summary, string text)
    {
        summary.Text = text;
        summary.IsVisible = true;
    }

    private static string Describe(ImportResult result)
    {
        var text = new StringBuilder();

        text.Append(result.Added switch
        {
            0 => "Не добавлено ничего.",
            1 => "Добавлен 1 сервер.",
            _ => $"Добавлено серверов: {result.Added}.",
        });

        if (result.Duplicates > 0)
            text.Append($" Уже были в списке: {result.Duplicates}.");

        foreach (var failure in result.SubscriptionErrors)
            text.Append($"\n{failure}");

        // Каждую непринятую строку называем по номеру и по причине. Сводка вида
        // «часть ссылок не подошла» бесполезна: править всё равно придётся руками,
        // и без номера строки искать будет негде.
        foreach (var error in result.Errors.Take(6))
            text.Append($"\nСтрока {error.Line}: {error.Error.Message}");

        if (result.Errors.Count > 6)
            text.Append($"\n…и ещё строк с ошибками: {result.Errors.Count - 6}");

        return text.ToString();
    }
}
