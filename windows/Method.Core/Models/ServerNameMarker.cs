// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Method.Core.Models;

/// <summary>
/// Разбор маркера выделения из имени узла.
///
/// Панель отдаёт подписку — список share-ссылок и несколько заголовков на всю
/// подписку целиком. Заголовка «выдели вот этот узел» не шлёт ни одна панель, да
/// и не может: заголовок относится к подписке, а не к строке в ней. Единственное,
/// что провайдер задаёт свободно поузлово и в любой панели без доработки, — имя
/// записи, то есть фрагмент после «#».
///
/// Поэтому маркер читается из имени, и распространённых форм поддерживается
/// сразу несколько, а не одна своя: провайдер уже как-то называет узлы, и
/// подстраиваться должен клиент.
///
/// Распознанный маркер из имени вычищается: оставленный на месте, он превратил бы
/// список в кашу из значков, рядом с которыми стоял бы ещё и наш собственный.
/// </summary>
public static class ServerNameMarker
{
    /// <summary>
    /// Символьные маркеры. Набор намеренно узкий: чем он шире, тем чаще
    /// декоративная эмодзи в имени случайно делает узел «золотым».
    /// Флаги стран (🇺🇸, 🇫🇮) сюда не входят и не вырезаются никогда.
    /// </summary>
    private static readonly HashSet<int> Symbols =
    [
        0x2B50,  // ⭐
        0x1F31F, // 🌟
        0x2728,  // ✨
        0x1F451, // 👑
        0x1F48E, // 💎
        0x1F3C6, // 🏆
        0x1F947, // 🥇
        0x2605,  // ★
        0x2606,  // ☆
        0x2726,  // ✦
        0x272A,  // ✪
        0x2730,  // ✰
    ];

    /// <summary>
    /// Словесные маркеры. Ищутся по границе слова, без учёта регистра, в скобках
    /// любого вида и без них: «[VIP]», «(Premium)», «【VIP】», «US · GOLD», «Премиум».
    /// </summary>
    private static readonly string[] Words =
    [
        "vip", "premium", "prem", "gold", "pro",
        "премиум", "вип", "голд",
    ];

    private static readonly Regex WordRegex = new(
        @"(?:[\[\(\{【〔<]\s*)?\b(?:" + string.Join("|", Words) + @")\b\+?(?:\s*[\]\)\}】〕>])?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Пустые скобки, остающиеся после вырезания маркера.</summary>
    private static readonly Regex LitterRegex = new(
        @"[\[\(\{【〔<]\s*[\]\)\}】〕>]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string LabelTrim = "[](){}【】〔〕<> \t+";
    private const string EdgeTrim = " \t-–—·|,:;/";

    /// <summary>
    /// Разобрать имя: вернуть очищенное имя и метку выделения. Метка пустая —
    /// обычный сервер.
    /// </summary>
    public static (string Name, string? Label) Strip(string raw)
    {
        var original = (raw ?? "").Trim();
        if (original.Length == 0) return (original, null);

        string? label = null;
        var work = original;

        // 1. Словесный маркер. Он информативнее символа, поэтому имеет приоритет
        //    при выборе того, что показать человеку.
        var match = WordRegex.Match(work);
        if (match.Success)
        {
            label = match.Value.Trim(LabelTrim.ToCharArray()).ToUpperInvariant();
        }
        work = WordRegex.Replace(work, " ");

        // 2. Символьные маркеры — по одному видимому знаку, чтобы не зависеть от
        //    того, записана эмодзи с селектором начертания (U+FE0F) или без него.
        var kept = new StringBuilder(work.Length);
        var elements = StringInfo.GetTextElementEnumerator(work);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (TryMarkerRune(element, out var baseRune))
            {
                label ??= baseRune.ToString();
                kept.Append(' ');
            }
            else
            {
                kept.Append(element);
            }
        }
        work = kept.ToString();

        if (label is null) return (original, null);

        // 3. Уборка: пустые скобки, сдвоенные пробелы, висящие разделители.
        work = LitterRegex.Replace(work, " ");
        work = CollapseSpaces(work);
        work = work.Trim(EdgeTrim.ToCharArray());
        work = CollapseSpaces(work);

        // Имя, состоявшее из одного маркера, чистить не во что — лучше показать
        // как есть, чем пустую строку.
        return (work.Length == 0 ? original : work, label);
    }

    private static bool TryMarkerRune(string element, out Rune baseRune)
    {
        baseRune = default;
        var runes = element.EnumerateRunes();
        if (!runes.MoveNext()) return false;

        var first = runes.Current;
        if (!Symbols.Contains(first.Value)) return false;

        // Допускаются только селекторы начертания: «⭐️» и «⭐» — одно и то же.
        while (runes.MoveNext())
        {
            var v = runes.Current.Value;
            if (v != 0xFE0F && v != 0xFE0E) return false;
        }

        baseRune = first;
        return true;
    }

    private static string CollapseSpaces(string s) =>
        string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
