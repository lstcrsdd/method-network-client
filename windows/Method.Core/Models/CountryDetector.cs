// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text;
using System.Text.RegularExpressions;

namespace Method.Core.Models;

/// <summary>Страна узла и имя без её обозначения.</summary>
/// <param name="Code">Двухбуквенный код или пусто, если не опознали.</param>
/// <param name="Name">Название страны для человека. Пусто, если код неизвестен.</param>
/// <param name="CleanName">Имя узла без флага, который мы теперь рисуем отдельно.</param>
public sealed record CountryMatch(string? Code, string? Name, string CleanName);

/// <summary>
/// Определение страны по имени узла.
///
/// <para>Другого источника нет: в профиле страны не бывает, а спрашивать её у
/// внешней службы по адресу узла значит рассказывать посторонним, к каким
/// серверам подключается человек. Имя же провайдер задаёт сам и почти всегда
/// кладёт туда либо флаг, либо код.</para>
///
/// <para>Порядок проверок — от надёжного к рискованному. Флаг однозначен,
/// название страны однозначно, двухбуквенный код — уже догадка: «MY-NODE»
/// содержит код Малайзии, ничего про неё не означая. Поэтому код принимается
/// только отдельным словом и только заглавными, а ошибка здесь стоит неверного
/// флажка, а не неверного подключения.</para>
/// </summary>
public static class CountryDetector
{
    public static CountryMatch Detect(string rawName)
    {
        var name = (rawName ?? "").Trim();
        if (name.Length == 0) return new CountryMatch(null, null, name);

        // 1. Флаг эмодзи — пара региональных указателей. Однозначно и вырезается
        //    из имени: рисовать его мы будем сами, а дважды показывать незачем.
        if (TryReadFlag(name, out var flagCode, out var withoutFlag))
            return new CountryMatch(flagCode, NameOf(flagCode), Tidy(withoutFlag));

        // 2. Название страны словом.
        foreach (var (code, words) in Names)
            foreach (var word in words)
                if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return new CountryMatch(code, NameOf(code), name);

        // 3. Двухбуквенный код отдельным словом.
        foreach (Match token in TokenRegex.Matches(name))
        {
            var candidate = token.Value;
            if (Names.ContainsKey(candidate)) return new CountryMatch(candidate, NameOf(candidate), name);
        }

        return new CountryMatch(null, null, name);
    }

    /// <summary>Пара региональных указателей — это и есть флаг.</summary>
    private static bool TryReadFlag(string name, out string code, out string rest)
    {
        code = "";
        rest = name;

        var runes = name.EnumerateRunes().ToArray();

        for (var i = 0; i + 1 < runes.Length; i++)
        {
            if (!IsRegionalIndicator(runes[i]) || !IsRegionalIndicator(runes[i + 1])) continue;

            code = string.Concat(
                (char)('A' + runes[i].Value - 0x1F1E6),
                (char)('A' + runes[i + 1].Value - 0x1F1E6));

            var builder = new StringBuilder();
            for (var j = 0; j < runes.Length; j++)
            {
                if (j == i || j == i + 1) continue;
                builder.Append(runes[j].ToString());
            }

            rest = builder.ToString();
            return true;
        }

        return false;
    }

    private static bool IsRegionalIndicator(System.Text.Rune rune) =>
        rune.Value is >= 0x1F1E6 and <= 0x1F1FF;

    /// <summary>Убрать разделители, повисшие на месте вырезанного флага.</summary>
    private static string Tidy(string value)
    {
        var collapsed = string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim(' ', '\t', '-', '–', '—', '·', '|', ',');
    }

    private static readonly Regex TokenRegex = new(
        @"(?<![A-Za-z])[A-Z]{2}(?![A-Za-z])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? NameOf(string? code) =>
        code is not null && Names.TryGetValue(code, out var words) ? words[0] : null;

    /// <summary>
    /// Названия стран. Первое — то, что увидит человек; остальные нужны для
    /// опознания по имени узла, поэтому здесь и русские, и английские.
    /// </summary>
    private static readonly Dictionary<string, string[]> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AT"] = ["Австрия", "Austria"],
        ["AU"] = ["Австралия", "Australia"],
        ["BE"] = ["Бельгия", "Belgium"],
        ["BG"] = ["Болгария", "Bulgaria"],
        ["BR"] = ["Бразилия", "Brazil"],
        ["CA"] = ["Канада", "Canada"],
        ["CH"] = ["Швейцария", "Switzerland"],
        ["CZ"] = ["Чехия", "Czechia"],
        ["DE"] = ["Германия", "Germany"],
        ["DK"] = ["Дания", "Denmark"],
        ["EE"] = ["Эстония", "Estonia"],
        ["ES"] = ["Испания", "Spain"],
        ["FI"] = ["Финляндия", "Finland"],
        ["FR"] = ["Франция", "France"],
        ["GB"] = ["Великобритания", "United Kingdom"],
        ["HK"] = ["Гонконг", "Hong Kong"],
        ["HU"] = ["Венгрия", "Hungary"],
        ["IE"] = ["Ирландия", "Ireland"],
        ["IL"] = ["Израиль", "Israel"],
        ["IN"] = ["Индия", "India"],
        ["IS"] = ["Исландия", "Iceland"],
        ["IT"] = ["Италия", "Italy"],
        ["JP"] = ["Япония", "Japan"],
        ["KZ"] = ["Казахстан", "Kazakhstan"],
        ["LT"] = ["Литва", "Lithuania"],
        ["LU"] = ["Люксембург", "Luxembourg"],
        ["LV"] = ["Латвия", "Latvia"],
        ["MD"] = ["Молдова", "Moldova"],
        ["NL"] = ["Нидерланды", "Netherlands"],
        ["NO"] = ["Норвегия", "Norway"],
        ["PL"] = ["Польша", "Poland"],
        ["PT"] = ["Португалия", "Portugal"],
        ["RO"] = ["Румыния", "Romania"],
        ["RS"] = ["Сербия", "Serbia"],
        ["RU"] = ["Россия", "Russia"],
        ["SE"] = ["Швеция", "Sweden"],
        ["SG"] = ["Сингапур", "Singapore"],
        ["SK"] = ["Словакия", "Slovakia"],
        ["TR"] = ["Турция", "Turkey"],
        ["UA"] = ["Украина", "Ukraine"],
        ["US"] = ["США", "United States"],
    };
}
