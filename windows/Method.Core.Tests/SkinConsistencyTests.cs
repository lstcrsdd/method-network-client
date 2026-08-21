// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.RegularExpressions;
using Xunit;

namespace Method.Core.Tests;

/// <summary>
/// Сторожа договора о двух оформлениях.
///
/// <para>Оформление — это набор значений, подменяемый на ходу поверх общих
/// стилей. Договор держится на трёх условиях, и каждое из них уже нарушалось
/// молча: ошибки не роняют сборку и не видны в одном оформлении — они вылезают
/// в другом, у человека на экране.</para>
///
/// <para>Проверки читают саму разметку, а не поведение: поднять окно в тесте
/// стоило бы графической подсистемы, а поймать надо ровно те три вещи, которые
/// видны в тексте файлов.</para>
/// </summary>
public class SkinConsistencyTests
{
    /// <summary>
    /// Наборы обязаны объявлять один и тот же перечень ключей.
    ///
    /// <para>Разметка одна на оба оформления. Ключ, объявленный только в одном,
    /// в другом даёт пустое место вместо цвета — и заметит это не сборка, а
    /// человек, переключивший оформление.</para>
    /// </summary>
    [Fact]
    public void Оформления_объявляют_одинаковый_набор_ключей()
    {
        var basic = KeysOf(Styles("Theme.axaml"));
        var poster = KeysOf(Styles("Theme.Poster.axaml"));

        var missingInPoster = basic.Except(poster).OrderBy(k => k).ToList();
        var missingInBasic = poster.Except(basic).OrderBy(k => k).ToList();

        Assert.True(
            missingInPoster.Count == 0 && missingInBasic.Count == 0,
            $"Наборы разошлись. Нет в плакатном: {Join(missingInPoster)}. Нет в основном: {Join(missingInBasic)}.");
    }

    /// <summary>
    /// Страницы и окна не объявляют собственных стилей.
    ///
    /// <para>Стиль, объявленный внутри страницы, сильнее набора приложения и
    /// молча перебивает оформление. Ровно на этом плакатный набор однажды не
    /// доехал до кнопки подключения: она просила печать, а страница отвечала
    /// радиусом семьдесят шесть, и побеждала страница.</para>
    /// </summary>
    [Fact]
    public void Разметка_не_объявляет_стилей_мимо_общего_набора()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppRoot(), "Views"), "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("<Style Selector=", StringComparison.Ordinal)) continue;

            offenders.Add(Path.GetFileName(file));
        }

        Assert.True(
            offenders.Count == 0,
            $"Стили объявлены мимо общего набора: {Join(offenders)}. " +
            "Их место в Styles/Controls.axaml — иначе оформление до них не дотянется.");
    }

    /// <summary>
    /// Образцы на карточках выбора оформления показывают настоящие цвета наборов.
    ///
    /// <para>Кнопка выбора рисует палитру того оформления, которое сейчас НЕ
    /// включено, поэтому цвета там записаны числом, а не ключом. Числа живут
    /// отдельно от наборов и однажды уже отстали: акцент в наборе поменяли, а
    /// образец остался показывать прежний.</para>
    /// </summary>
    [Fact]
    public void Образцы_палитры_совпадают_с_наборами()
    {
        var settings = File.ReadAllText(Path.Combine(AppRoot(), "Views", "Pages", "SettingsPage.axaml"));

        foreach (var theme in new[] { "Theme.axaml", "Theme.Poster.axaml" })
        {
            var accent = AccentOf(Styles(theme));

            Assert.True(
                settings.Contains(accent, StringComparison.OrdinalIgnoreCase),
                $"Образец оформления отстал от набора: акцент {accent} из {theme} " +
                "на карточке выбора не показан.");
        }
    }

    // ── Подсобное ──

    private static string Styles(string file) => Path.Combine(AppRoot(), "Styles", file);

    /// <summary>Корень приложения ищется подъёмом: тесты запускаются из bin.</summary>
    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Method.App");
            if (Directory.Exists(Path.Combine(candidate, "Styles"))) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Не найден каталог Method.App — проверять нечего.");
    }

    private static HashSet<string> KeysOf(string file) =>
        Regex.Matches(File.ReadAllText(file), "x:Key=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string AccentOf(string file)
    {
        var text = File.ReadAllText(file);
        var match = Regex.Match(text, "x:Key=\"AccentBrush\"\\s+Color=\"(#[0-9A-Fa-f]{6})\"");

        Assert.True(match.Success, $"В {Path.GetFileName(file)} нет AccentBrush с цветом числом.");
        return match.Groups[1].Value;
    }

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "—" : string.Join(", ", list);
    }
}
