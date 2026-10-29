// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

namespace Method.App.Services;

/// <summary>Чем кончился шаг. От этого зависит и метка справа, и её цвет.</summary>
public enum JournalOutcome
{
    /// <summary>Шаг идёт: ответа ещё нет.</summary>
    Waiting,

    /// <summary>Вышло.</summary>
    Ok,

    /// <summary>Шаг прошёл, и делать по нему нечего: узел не поднят, очередь пуста.</summary>
    Idle,

    /// <summary>Работать можно, но не так, как задумано.</summary>
    Warning,

    /// <summary>Не вышло.</summary>
    Failed,
}

/// <summary>Строка журнала: слева название шага, справа чем кончился.</summary>
/// <param name="Times">Сколько раз подряд пришла ровно эта строка.</param>
public sealed record JournalLine(string Label, string Value, JournalOutcome Outcome, int Times = 1)
{
    /// <summary>Ответ так, как его показывать: с числом повторов, если они были.</summary>
    public string Shown => Times > 1 ? $"{Value} ×{Times}" : Value;

    /// <summary>
    /// Та же запись: шаг, ответ и исход совпадают. Число повторов не в счёт —
    /// именно его такая запись и увеличивает.
    /// </summary>
    public bool SameAs(JournalLine other) =>
        string.Equals(Label, other.Label, StringComparison.OrdinalIgnoreCase)
        && Value == other.Value
        && Outcome == other.Outcome;
}

/// <summary>
/// Журнал работы: что клиент делает и чем это кончается.
///
/// <para>При старте он расшифровывает хранилище, читает правила, спрашивает
/// привилегированную часть и выясняет, не поднят ли туннель ещё до него. При
/// подключении — собирает конфигурацию, отдаёт её службе, включает защиту,
/// проверяет внешний адрес. Всё это он делал молча, и со стороны выглядело так,
/// будто окно просто открылось, а кнопка просто позеленела. Журнал показывает
/// работу, которая и так идёт: он ничего не изображает и ничего не задерживает.</para>
///
/// <para>Источник один на всех потребителей — заставку запуска и ленту в окне.
/// Разные источники разошлись бы в первый же день: событие, дописанное в одном
/// месте, во втором просто не появилось бы.</para>
///
/// <para>Статический, как и переключатель оформления: писать в него нужно из
/// модели, из окна и из служебных проверок, и протаскивать один и тот же
/// экземпляр через все три значило бы менять их подписи ради строки текста.</para>
/// </summary>
public static class WorkJournal
{
    private static readonly List<JournalLine> Records = [];
    private static readonly object Guard = new();

    /// <summary>Все накопленные строки в порядке появления.</summary>
    public static IReadOnlyList<JournalLine> Lines
    {
        get { lock (Guard) return Records.ToArray(); }
    }

    /// <summary>Строка добавлена. Заставка печатает её, не дожидаясь остальных.</summary>
    public static event Action<JournalLine>? Added;

    /// <summary>
    /// Проверки кончились. Не «журнал закрыт»: писать в него можно и дальше, но
    /// заставке пора уходить — дальше человек смотрит на окно, а не на строки.
    /// </summary>
    public static event Action? Finished;

    /// <summary>
    /// Добавить строку. Если она слово в слово повторяет последнюю, новой строки
    /// не заводится: у последней растёт счётчик повторов. Сорок одинаковых строк
    /// подряд — это не сорок событий, а одно, и столбец из них вытесняет из
    /// ленты всё остальное.
    /// </summary>
    public static void Add(string label, string value, JournalOutcome outcome = JournalOutcome.Ok)
    {
        var line = new JournalLine(label, value, outcome);

        lock (Guard)
        {
            if (Records.Count > 0 && Records[^1].SameAs(line))
            {
                line = Records[^1] with { Times = Records[^1].Times + 1 };
                Records[^1] = line;
            }
            else
            {
                Records.Add(line);
            }

            // Тот же расчёт, что и у ленты: журнал нужен для показа, а не для
            // разбирательств — для них есть журнал службы на диске.
            if (Records.Count > 400) Records.RemoveRange(0, 200);
        }

        Added?.Invoke(line);
    }

    /// <summary>
    /// Дописать ответ к шагу, который начинали с ожидания. Строка остаётся на
    /// своём месте: шаги идут в том порядке, в каком их начали, и ответ,
    /// приехавший последним, не должен уводить свой шаг в конец списка.
    /// </summary>
    public static void Resolve(string label, string value, JournalOutcome outcome)
    {
        JournalLine updated;

        lock (Guard)
        {
            // Сравниваем без оглядки на регистр: заставка печатает метки
            // прописными, и один вызов, записанный так же, заводил бы вторую
            // строку вместо ответа на первую.
            var index = Records.FindLastIndex(r => string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase));
            updated = new JournalLine(label, value, outcome);

            if (index < 0) Records.Add(updated);
            else Records[index] = updated;
        }

        Added?.Invoke(updated);
    }

    public static void Finish() => Finished?.Invoke();
}
