// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;
using System.Text;

namespace Method.Core.Tunnel;

/// <summary>
/// Обрезка длинного журнала — по времени, а не только по размеру.
///
/// <para>Зачем именно так. Движок на уровне info пишет каждое соединение и каждый
/// ответ DNS — порядка полутора мегабайт в час при обычной работе. Обрезка
/// «пополам при мегабайте» оставляла в журнале от семнадцати до тридцати пяти
/// минут истории. А разбор приходит позже сбоя: человек говорит «час назад
/// отвалилось» и присылает журнал, в котором нужного момента уже нет. По горячим
/// следам такая диагностика работает, по обращению — нет.</para>
///
/// <para>Правило: хранить не меньше <see cref="DefaultRetention"/>, а если за этот
/// срок записей мало — добирать до <see cref="DefaultKeepBytes"/>, чтобы не
/// выбрасывать полезное впустую.</para>
///
/// <para><b>Верхняя граница остатка обязательна.</b> Обрезка вызывается из самого
/// метода записи. Если после неё файл остался бы больше порога, следующая же
/// строка снова запустила бы чтение и перезапись всего файла — и так на каждую
/// строку. Под нагрузкой это встало бы колом. Поэтому после обрезки гарантирован
/// запас до порога.</para>
/// </summary>
public static class LogRotation
{
    public const string TrimMarker = "…(журнал обрезан: удалены записи старше срока хранения)";

    /// <summary>Минимальная глубина истории.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(3);

    /// <summary>Порог, при котором вообще запускается обрезка.</summary>
    public const long DefaultMaxBytes = 32L * 1024 * 1024;

    /// <summary>Сколько оставлять, если за срок хранения записей меньше этого объёма.</summary>
    public const long DefaultKeepBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Потолок остатка. Три часа влезают сюда даже при восьми мегабайтах в час —
    /// втрое многословнее замеренного. Заодно это и есть тот запас, который не даёт
    /// обрезке срабатывать на каждой строке.
    /// </summary>
    public const long DefaultMaxKeepBytes = 24L * 1024 * 1024;

    /// <summary>
    /// Обрезает файл, если он перерос <paramref name="maxBytes"/>. Когда резать
    /// нечего, обходится проверкой длины файла.
    /// </summary>
    public static void TrimIfNeeded(
        string path,
        DateTimeOffset now,
        TimeSpan? retention = null,
        long maxBytes = DefaultMaxBytes,
        long keepBytes = DefaultKeepBytes,
        long maxKeepBytes = DefaultMaxKeepBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= maxBytes) return;

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return;

        var cutoff = now - (retention ?? DefaultRetention);

        // Первая строка, которую срок хранения велит сохранить. Строки без метки
        // времени (продолжение многострочной записи) принадлежат предыдущей записи,
        // поэтому границу задают только строки с меткой.
        var start = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            if (TryReadTimestamp(lines[i], out var stamp) && stamp >= cutoff)
            {
                start = i;
                break;
            }
        }

        // За срок хранения записей мало — добираем более старыми, чтобы не
        // выбрасывать полезное впустую.
        start = MoveBackTo(lines, start, keepBytes);

        // И наоборот: слишком много — режем с головы, иначе следующая строка снова
        // запустит перезапись всего файла. Метка об обрезке входит в бюджет: она
        // тоже окажется в файле, и без её учёта остаток вылезал бы за потолок.
        var budget = Math.Max(0, maxKeepBytes - ByteSize(TrimMarker));
        start = MoveForwardTo(lines, start, budget);

        if (start <= 0) return;

        var kept = new StringBuilder();
        kept.Append(TrimMarker).Append('\n');
        for (var i = start; i < lines.Length; i++) kept.Append(lines[i]).Append('\n');

        File.WriteAllText(path, kept.ToString());
    }

    /// <summary>Сдвинуть границу назад, пока остаток меньше нужного объёма.</summary>
    private static int MoveBackTo(string[] lines, int start, long minBytes)
    {
        var size = SizeFrom(lines, start);
        while (start > 0 && size < minBytes)
        {
            start--;
            size += ByteSize(lines[start]);
        }
        return start;
    }

    /// <summary>Сдвинуть границу вперёд, пока остаток больше допустимого.</summary>
    private static int MoveForwardTo(string[] lines, int start, long maxBytes)
    {
        var size = SizeFrom(lines, start);
        while (start < lines.Length && size > maxBytes)
        {
            size -= ByteSize(lines[start]);
            start++;
        }
        return start;
    }

    private static long SizeFrom(string[] lines, int start)
    {
        long size = 0;
        for (var i = start; i < lines.Length; i++) size += ByteSize(lines[i]);
        return size;
    }

    /// <summary>
    /// Размер строки в файле — в байтах, а не в символах.
    ///
    /// Считать длиной строки нельзя, и это не придирка: записи здесь на русском, а
    /// в UTF-8 кириллическая буква занимает два байта. При счёте в символах остаток
    /// после обрезки оказывался вдвое больше задуманного, то есть выходил за
    /// порог, — и следующая же строка снова запускала перезапись всего файла. Ровно
    /// то, от чего потолок и поставлен.
    /// </summary>
    private static long ByteSize(string line) => Encoding.UTF8.GetByteCount(line) + 1;

    /// <summary>
    /// Метка времени в начале строки. Формат — круглый (ISO 8601), тот же, которым
    /// журнал и пишется.
    /// </summary>
    private static bool TryReadTimestamp(string line, out DateTimeOffset stamp)
    {
        stamp = default;

        var space = line.IndexOf(' ');
        var head = space < 0 ? line : line[..space];
        if (head.Length == 0) return false;

        return DateTimeOffset.TryParse(
            head, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out stamp);
    }
}
