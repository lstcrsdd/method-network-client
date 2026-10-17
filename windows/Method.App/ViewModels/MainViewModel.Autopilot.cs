// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia.Threading;
using Method.App.Services;
using Method.Core.Core;
using Method.Core.Models;
using Method.Core.Native;
using Method.Core.Services;
using Method.Core.Storage;

namespace Method.App.ViewModels;

/// <summary>
/// Автоматика: клиент меряет маршруты, подаёт замеры ядру и исполняет его
/// решения.
///
/// <para>Ядро само не измеряет и не переключает — оно одно на все платформы, а
/// измеряют и переключают везде своим. Наша часть договора простая: давать
/// честные числа, честно исполнять решения и честно докладывать об исполнении.
/// Молчание маршрута подаётся как отказ, а не пропускается: иначе у мёртвого
/// узла осталась бы прежняя хорошая оценка, и ядро выбрало бы именно его.</para>
///
/// <para>Сам порядок «спросить — сделать — подтвердить» и придержка на
/// разогреве живут в <see cref="LanePilot"/>: там их можно проверить, здесь —
/// нет. Этому файлу остаётся завести всё это на подключении, остановить на
/// отключении и рассказать человеку, что произошло.</para>
/// </summary>
public sealed partial class MainViewModel
{
    private CoreEngine? _core;
    private CancellationTokenSource? _probes;
    private Task? _probeTask;
    private ProbeLoop? _loop;
    private int _lastAlive = -1;

    /// <summary>Дескриптор ядра → тег исходящего, человеческое имя узла и его адрес.</summary>
    private readonly Dictionary<uint, (string Tag, string Name, string Host)> _routes = [];

    /// <summary>Уже сказали, что ядро не может выбрать. Второй раз — шум.</summary>
    private bool _toldEmpty;

    /// <summary>
    /// Ядро на этом подключении уже хоть раз выбрало маршрут. До этого его
    /// «живых маршрутов нет» — не тревога, а прогрев: замеров мало, и ядро
    /// честно не доверяет никому.
    /// </summary>
    private bool _engaged;

    /// <summary>Уже сказали, что круги срываются. То же самое — второй раз не пишем.</summary>
    private bool _toldBroken;

    /// <summary>История измерений между запусками.</summary>
    private readonly CoreStateStore _history = CoreStateStore.Default(new DpapiSecretBox());

    /// <summary>Кругов с прошлого сохранения.</summary>
    private int _sinceSaved;

    /// <summary>
    /// Через сколько кругов history сбрасывается на диск. Не каждый круг: запись
    /// идёт с шифрованием и заменой файла, а накопленное между сохранениями
    /// стоит меньше, чем лишняя нагрузка на диск. Но и не только при
    /// отключении: клиент могут закрыть через диспетчер задач или выключить
    /// машину кнопкой, и тогда вся история пропала бы.
    /// </summary>
    private const int SaveEvery = 10;

    /// <summary>Имя узла по тегу: в ленте человек читает «Литва», а не «lane-2be23608».</summary>
    private string Named(string tag) =>
        _routes.Values.FirstOrDefault(r => r.Tag == tag).Name ?? tag;

    /// <summary>
    /// Завести контур на этом подключении.
    ///
    /// <para>Каталог маршрутов берётся у сборщика конфигурации, а не собирается
    /// заново: мерить надо ровно то, что попало в группу. Соберись он тут по
    /// тому же правилу — правило жило бы в двух местах и разошлось бы молча.</para>
    /// </summary>
    private void StartAutopilot(
        ServerProfile main, SingBoxConfigBuilder.Options options, ClashApiClient clash)
    {
        if (!CoreEngine.Available) return;

        var members = SingBoxConfigBuilder.AutoMembers(main, options);
        if (members.Count < 2) return;

        CoreEngine core;
        try
        {
            core = new CoreEngine(ProbeLoop.SteadyStep);
        }
        catch (Exception e)
        {
            // Без ядра клиент работает: выбор остаётся за движком. Молчать всё
            // же нельзя — человек включил автоматику и вправе знать, что она
            // сейчас проще обещанной.
            WorkJournal.Add("Автоматика", "ядро не поднялось: " + e.Message, JournalOutcome.Warning);
            return;
        }

        var targets = new List<ProbeTarget>(members.Count);

        _routes.Clear();

        foreach (var (tag, node) in members)
        {
            var route = core.AddRoute(RouteCatalog.Describe(node));

            targets.Add(new ProbeTarget(tag, route));
            _routes[route] = (tag, node.Name, node.Host);
        }

        core.AddLane(new CoreLane("web", "Весь трафик"));

        // История с прошлого запуска — до первых замеров. Без неё ядро несколько
        // минут не доверяет ни одному маршруту, и выбор всё это время делать
        // попросту не на чем. Записи, у которых ось не совпала с объявленной,
        // ядро отбросит само: это другой маршрут под старым именем.
        Recall(core);

        // Полоса стоит на узле, выбранном человеком: так собрана группа.
        var pilot = new LanePilot(
            core, clash, SingBoxConfigBuilder.AutoTag,
            _routes.ToDictionary(r => r.Key, r => r.Value.Tag),
            SingBoxConfigBuilder.ProxyTag);

        pilot.Moved += move => Switched(Named(move.Tag), move.Reason);
        pilot.Refused += move => Refused(Named(move.Tag));
        pilot.Held += Held;
        pilot.Kept += move => Kept(Named(move.Tag));

        var loop = new ProbeLoop(clash, core, targets)
        {
            // Применение и сохранение — оба внутри круга: и то и другое трогает
            // ядро, а оно не терпит одновременных вызовов из двух потоков.
            AfterRound = async (round, token) =>
            {
                await pilot.ApplyAsync(round, token).ConfigureAwait(false);

                if (++_sinceSaved < SaveEvery) return;

                _sinceSaved = 0;
                Remember(core);
            },
        };
        loop.RoundDone += Report;
        loop.RoundFailed += Broke;

        _core = core;
        _loop = loop;
        _toldEmpty = false;
        _engaged = false;
        _toldBroken = false;

        // Проверка узлов могла закончиться до подключения: что она нашла, контур
        // должен знать с первого же круга.
        SyncSilent();
        _lastAlive = -1;
        _sinceSaved = 0;

        _probes = new CancellationTokenSource();

        // Контур живёт своим чередом и окно не держит: круг длится секунды, и
        // ждать его в потоке интерфейса значило бы подвесить окно на замерах.
        _probeTask = loop.RunAsync(_probes.Token);

        WorkJournal.Add("Замеры", $"{targets.Count} маршрутов, шаг {ProbeLoop.WarmUpStep.TotalSeconds:F0} с");
    }

    /// <summary>
    /// Остановить контур: туннель опущен — мерить нечего и не через что.
    ///
    /// <para>Память ядра освобождается только после того, как контур
    /// остановился. Освободить её на ходу — это обращение к освобождённому
    /// указателю из чужого потока, то есть падение всего клиента без следа в
    /// журнале. Не дождались — оставляем экземпляр висеть: утечка одного
    /// движка на подключение неприятна, но она чинится перезапуском, а падение
    /// посреди работы — нет.</para>
    /// </summary>
    private async Task StopAutopilotAsync()
    {
        var probes = _probes;
        var task = _probeTask;
        var core = _core;

        // Отключились, пока ядро прогревалось, — строка ожидания не должна
        // висеть с «жду» до конца сеанса: ждать больше нечего.
        if (!_engaged && _toldEmpty)
            WorkJournal.Resolve("Маршрут", "отключено до первого решения ядра", JournalOutcome.Idle);

        _probes = null;
        _probeTask = null;
        _core = null;
        _loop = null;
        _lastAlive = -1;
        _toldEmpty = false;
        _engaged = false;
        _toldBroken = false;
        _routes.Clear();

        if (probes is null) return;

        await probes.CancelAsync().ConfigureAwait(true);

        var stopped = task is null;

        if (task is not null)
        {
            // Замер отменяется вместе с запросом, так что ожидание короткое.
            // Срок всё равно ставим: зависший вызов не должен держать отключение.
            stopped = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)))
                .ConfigureAwait(true) == task;
        }

        probes.Dispose();

        if (!stopped)
        {
            // Контур ещё внутри вызова к ядру: и сохранять, и освобождать сейчас
            // значит лезть туда же вторым потоком.
            WorkJournal.Add("Замеры", "контур не остановился в срок", JournalOutcome.Warning);
            return;
        }

        if (core is null) return;

        // Историю снимаем последним делом перед освобождением: это самый полный
        // её вид за сессию.
        Remember(core);
        core.Dispose();
    }

    /// <summary>
    /// Вернуть ядру историю прошлых запусков.
    ///
    /// <para>Без неё клиент первые минуты после каждого подключения выбирает
    /// маршрут вслепую: ядро набирает уверенность десятками проб и до тех пор
    /// честно отвечает «выбирать не из чего». Неудача чтения поломкой не
    /// считается — просто наберём заново, как было до переноса.</para>
    /// </summary>
    private void Recall(CoreEngine core)
    {
        var saved = _history.Load();
        if (saved is null) return;

        try
        {
            core.LoadState(saved.State, (ulong)Environment.TickCount64, (ulong)saved.Elapsed.TotalMilliseconds);

            WorkJournal.Add("Память замеров", saved.Elapsed < TimeSpan.FromHours(1)
                ? "восстановлена"
                : $"восстановлена, ей {Age(saved.Elapsed)}");
        }
        catch (Exception e)
        {
            // Ядро отвергло выгрузку — другой её формат, испорченное
            // содержимое. Работать это не мешает, молчать об этом не стоит.
            WorkJournal.Add("Память замеров", "не принята ядром: " + e.Message, JournalOutcome.Warning);
        }
    }

    /// <summary>Сохранить историю. Зовётся только из потока контура.</summary>
    private void Remember(CoreEngine core)
    {
        try { _history.Save(core.SaveState((ulong)Environment.TickCount64)); }
        catch (Exception) { /* не сохранилось — наберём заново */ }
    }

    private static string Age(TimeSpan elapsed) => elapsed.TotalDays >= 1
        ? $"{elapsed.TotalDays:F0} сут"
        : $"{elapsed.TotalHours:F0} ч";

    /// <summary>
    /// Сеть сменилась — сказать об этом контуру. Сам он этого не видит, а ядро
    /// обесценивает накопленное только по такому слову.
    /// </summary>
    private void NoteNetworkChanged() => _loop?.NoteNetworkChanged();

    /// <summary>
    /// Передать контуру, какие адреса молчат по последней проверке узлов.
    /// Вызывается с потока окна: список узлов живёт на нём.
    /// </summary>
    /// <returns>Сколько маршрутов текущего подключения сняты с замеров; null — контура нет.</returns>
    private int? SyncSilent()
    {
        if (_loop is not { } loop) return null;

        var silent = Servers.Where(s => s.AddressSilent).Select(s => s.Profile.Host).ToHashSet();
        var tags = _routes.Values.Where(r => silent.Contains(r.Host)).Select(r => r.Tag).ToArray();

        loop.MarkSilent(tags);
        return tags.Length;
    }

    /// <summary>
    /// Доложить о круге — но только когда изменилось число живых маршрутов.
    /// Строка на каждый круг забила бы ленту одним и тем же: круги идут раз в
    /// несколько секунд, а лента обрезается, и осмысленные записи о подключении
    /// вытеснились бы отчётом о том, что всё по-прежнему.
    /// </summary>
    private void Report(ProbeRound round)
    {
        _toldBroken = false;

        if (round.Alive == _lastAlive) return;
        _lastAlive = round.Alive;

        // Контур крутится не на потоке окна, а лента живёт на нём.
        Dispatcher.UIThread.Post(() => WorkJournal.Add(
            "Замеры",
            round.Alive == round.Total
                ? $"отвечают все {round.Total}"
                : $"отвечают {round.Alive} из {round.Total}",
            round.Alive == 0 ? JournalOutcome.Failed
            : round.Alive == round.Total ? JournalOutcome.Ok
            : JournalOutcome.Warning));
    }

    /// <summary>
    /// Круг сорвался. Пишем один раз на подряд идущие срывы: контур продолжает
    /// работать, и повторять одну и ту же строку каждые полминуты — значит
    /// вытеснить из ленты всё остальное.
    /// </summary>
    private void Broke(Exception e)
    {
        if (_toldBroken) return;
        _toldBroken = true;

        Dispatcher.UIThread.Post(() => WorkJournal.Add("Замеры", "круг сорвался: " + e.Message, JournalOutcome.Failed));
    }

    /// <summary>
    /// Полоса переехала. Вместе с причиной, слово в слово от ядра: автоматика
    /// без объяснения читается как своеволие, и её выключают первой.
    /// </summary>
    private void Switched(string node, string reason) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Строка прогрева закрывается тем, что ядро заработало, а уже под
            // ней — куда оно повело полосу.
            if (!_engaged && _toldEmpty)
                WorkJournal.Resolve("Маршрут", "ядро набрало замеры", JournalOutcome.Ok);

            _engaged = true;
            _toldEmpty = false;

            WorkJournal.Add("Маршрут", node, JournalOutcome.Ok);
            WorkJournal.Add("Причина", reason);
        });

    /// <summary>
    /// Ядро выбрало то, что и так стоит. Говорим об этом один раз — при первом
    /// решении на подключении и при возвращении после «выбирать не из чего»:
    /// иначе человек не узнает, что автоматика вообще включилась, а молчание
    /// после строки прогрева читается как зависание.
    /// </summary>
    private void Kept(string node) =>
        Dispatcher.UIThread.Post(() =>
        {
            if ((_engaged && !_toldEmpty) || _loop is null) return;

            var text = $"ядро выбрало: остаётся {node}";

            // Ждали прогрева — дописываем ответ к той же строке, а не заводим
            // новую под ней.
            if (!_engaged && _toldEmpty) WorkJournal.Resolve("Маршрут", text, JournalOutcome.Ok);
            else WorkJournal.Add("Маршрут", text, JournalOutcome.Ok);

            _engaged = true;
            _toldEmpty = false;
        });

    /// <summary>Движок не принял переключение: маршрут остался прежним, и это надо видеть.</summary>
    private static void Refused(string node) =>
        Dispatcher.UIThread.Post(() =>
            WorkJournal.Add("Маршрут", $"переключить на {node} не вышло", JournalOutcome.Failed));

    /// <summary>
    /// Ядру не из чего выбирать. Говорим один раз: пока положение не изменилось,
    /// повторять это каждые полминуты значит вытеснить из ленты всё остальное.
    ///
    /// <para>До первого решения на подключении это прогрев, а не тревога: ядро
    /// не доверяет короткой серии замеров и отвечает «живых маршрутов нет —
    /// трафик блокируется». Показать эти слова как есть — значит напугать
    /// человека тем, чего не происходит: блокировку клиент не исполняет, стоит
    /// его узел. Поэтому до первого решения — строка ожидания, которую закроет
    /// само решение; после — предупреждение со словами ядра, потому что тогда
    /// маршруты и правда перестали отвечать.</para>
    /// </summary>
    private void Held(string reason) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Контур уже остановлен — это эхо прежнего подключения.
            if (_toldEmpty || _loop is null) return;
            _toldEmpty = true;

            if (_engaged)
                WorkJournal.Add("Маршрут", "оставлен ручной выбор: " + reason, JournalOutcome.Warning);
            else
                WorkJournal.Add("Маршрут", "ядро набирает замеры, пока стоит ручной выбор", JournalOutcome.Waiting);
        });
}
