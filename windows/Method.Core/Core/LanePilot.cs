// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.RegularExpressions;
using Method.Core.Native;
using Method.Core.Services;

namespace Method.Core.Core;

/// <summary>Полоса переехала (или не смогла) — тег маршрута и слова ядра.</summary>
public sealed record LaneMove(string Tag, string Reason);

/// <summary>
/// Исполнитель решений ядра: спросить, сделать, подтвердить.
///
/// <para>Именно в таком порядке, и подтверждение — не формальность. Ядро
/// нарочно ничего не считает исполненным само: только исполнитель знает, дошёл
/// ли его вызов до движка. Подтверди наперёд — и ядро будет считать
/// поставленным маршрут, которого на полосе нет, а трафик пойдёт прежним
/// путём, причём молча.</para>
///
/// <para><b>Пустая полоса.</b> Пока замеров мало, ядро не доверяет ни одному
/// маршруту и на вопрос отвечает буквально: «живых маршрутов у полосы не
/// осталось — трафик блокируется». Прямой выход оно не подставляет никогда, и
/// это верно. Но за нашей полосой стоит узел, выбранный человеком, и про него
/// ядро не знает: резать трафик вместо возврата к ручному выбору мы не будем —
/// ручное главнее автоматики и здесь тоже. Поэтому исполняется только
/// <see cref="CoreActionKind.Select"/>.</para>
///
/// <para><b>Своего порога уверенности здесь нет, и это осознанно.</b> Соблазн
/// придержать решения, «пока ядро не разогрелось», приводит ко второй системе
/// принятия решений, спорящей с первой: замерено, что порог 0,6 держал
/// автоматику выключенной и после того, как ядро уже назвало маршрут. Когда
/// маршруту можно верить, знает ядро — у него для этого свой порог участия и
/// ворота, а нам довольно правила «блокировку не исполняем».</para>
/// </summary>
public sealed class LanePilot
{
    private readonly CoreEngine _engine;
    private readonly ILaneSwitch _switch;
    private readonly string _lane;
    private readonly IReadOnlyDictionary<uint, string> _tags;

    /// <param name="lane">Тег группы в движке — та самая ручка.</param>
    /// <param name="tags">Дескриптор маршрута у ядра → тег исходящего в движке.</param>
    /// <param name="standing">Что стоит на полосе сейчас: группа собрана с этим умолчанием.</param>
    public LanePilot(
        CoreEngine engine,
        ILaneSwitch laneSwitch,
        string lane,
        IReadOnlyDictionary<uint, string> tags,
        string? standing = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _switch = laneSwitch ?? throw new ArgumentNullException(nameof(laneSwitch));
        _lane = lane ?? throw new ArgumentNullException(nameof(lane));
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));

        Standing = standing;
    }

    /// <summary>Что стоит на полосе по нашим сведениям.</summary>
    public string? Standing { get; private set; }

    /// <summary>Полоса переехала — и вот почему.</summary>
    public event Action<LaneMove>? Moved;

    /// <summary>Движок не принял переключение. Маршрут остался прежним, ядру не подтверждено.</summary>
    public event Action<LaneMove>? Refused;

    /// <summary>Ядру не из чего выбирать: остаётся то, что стоит.</summary>
    public event Action<string>? Held;

    /// <summary>Применить то, что ядро решило по итогам круга.</summary>
    public async Task ApplyAsync(ProbeRound round, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(round);

        foreach (var action in _engine.Reconcile((ulong)Environment.TickCount64))
        {
            if (ct.IsCancellationRequested) return;

            if (action.Kind != CoreActionKind.Select)
            {
                Held?.Invoke(Readable(action.Reason));
                continue;
            }

            // Дескриптор не из нашего каталога — брать нечего. Молча пропустить
            // безопаснее, чем гадать: неверный тег движок примет за незнакомый
            // и откажет, а мы решим, что дело в сети.
            if (!_tags.TryGetValue(action.Route, out var tag)) continue;

            var move = new LaneMove(tag, Readable(action.Reason));

            if (!await _switch.SelectAsync(_lane, tag, ct).ConfigureAwait(false))
            {
                // Не подтверждаем: для ядра полоса осталась прежней, и на
                // следующем круге оно предложит то же самое. Так и надо — молча
                // забыть про несделанное значит остаться на маршруте, который
                // ядро уже признало негодным.
                Refused?.Invoke(move);
                continue;
            }

            _engine.LaneApplied(action.Lane, action.Route);

            if (Standing == tag) continue;

            Standing = tag;
            Moved?.Invoke(move);
        }
    }

    /// <summary>
    /// Причина словами человека. Ядро называет маршрут вместе с опознавателем —
    /// «включаем Финляндию (f256de75…)», — и для него это правильно: два узла
    /// одной страны иначе не различить в отладочном выводе. Но в ленте клиента
    /// рядом со строкой уже стоит имя узла, а тридцать два знака шестнадцатеричной
    /// каши только съедают её ширину и обрываются многоточием.
    /// </summary>
    private static string Readable(string reason) => Identifier.Replace(reason, string.Empty).Trim();

    private static readonly Regex Identifier = new(@"\s*\([0-9a-fA-F]{32}\)", RegexOptions.Compiled);
}
