// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Method.Core.Native;
using Method.Core.Services;

namespace Method.Core.Core;

/// <summary>Маршрут глазами контура: чем его мерить и как он назван у ядра.</summary>
/// <param name="Tag">Тег исходящего в движке — им маршрут и меряется.</param>
/// <param name="Route">Дескриптор того же маршрута у ядра.</param>
public sealed record ProbeTarget(string Tag, uint Route);

/// <summary>Итог одного круга: сколько маршрутов ответило и какова уверенность ядра.</summary>
public sealed record ProbeRound(int Alive, int Total, float Confidence, bool WarmingUp);

/// <summary>
/// Измерительный контур: гоняет замеры по маршрутам и подаёт их ядру.
///
/// <para>Ядро само ничего не измеряет — это его свойство, а не недоделка: оно
/// одно на все платформы, а измеряет везде своё. Наша обязанность — давать ему
/// честные числа и не давать нечестных.</para>
///
/// <para>Меряет движок и настоящим маршрутом. Снаружи померить нельзя: TCP до
/// адреса узла идёт мимо туннеля и говорит о пути до сервера, а не о сервере
/// как о выходе, — со всеми рукопожатиями, обфускацией и тем, что творится на
/// той стороне.</para>
/// </summary>
public sealed class ProbeLoop
{
    /// <summary>
    /// Разогрев. Ядро не доверяет короткой серии: при десятке проб уверенность
    /// нулевая, и маршрут в выборе не участвует вовсе. Пока уверенности нет,
    /// выбирать не из чего — поэтому первые минуты меряем часто.
    ///
    /// <para>Разогрев — про частоту опроса, и только. Разрешать или запрещать
    /// решения по нему нельзя: когда маршруту можно верить, знает ядро, у него
    /// для этого свой порог участия и ворота. Свой порог поверх ядра — это
    /// вторая система принятия решений, которая спорит с первой; замерено, что
    /// он держал автоматику выключенной и после того, как ядро уже назвало
    /// маршрут.</para>
    /// </summary>
    public static readonly TimeSpan WarmUpStep = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Поддержание. Достаточно редко, чтобы не оставлять в сети ровный
    /// маячок, и достаточно часто, чтобы заметить, что маршрут отвалился.
    /// </summary>
    public static readonly TimeSpan SteadyStep = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Уверенность, начиная с которой частый опрос больше не нужен. Не единица:
    /// до полной ядро добирается сотней проб, и держать частый опрос всё это
    /// время значит светить маячком дольше, чем нужно.
    /// </summary>
    private const float WarmEnough = 0.5f;

    /// <summary>
    /// Разогрев — состояние, а не мгновенный ответ на текущую уверенность. В
    /// поддержании замеры стареют между кругами, и уверенность проседает ниже
    /// порога сама собой: считай её каждый раз заново — контур запрыгал бы
    /// между частым и редким опросом без конца. Обратно в разогрев возвращает
    /// только смена сети, где накопленное и правда обесценилось.
    /// </summary>
    private bool _warm = true;

    /// <summary>
    /// Сколько замеров идёт одновременно. Не ради скорости: мёртвый маршрут
    /// держит таймаут целиком, и в последовательном круге живые маршруты
    /// измерялись бы через десятки секунд после первых. Числа из такого круга
    /// относятся к разным моментам и сравнению между собой не подлежат, а
    /// ядро сравнивает именно их.
    /// </summary>
    private const int AtOnce = 3;

    private readonly IRouteDelaySource _source;
    private readonly CoreEngine _engine;
    private readonly IReadOnlyList<ProbeTarget> _targets;
    private readonly Random _jitter = new();

    public ProbeLoop(IRouteDelaySource source, CoreEngine engine, IReadOnlyList<ProbeTarget> targets)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    /// <summary>Круг закончен. Вызывается на потоке контура, а не на потоке окна.</summary>
    public event Action<ProbeRound>? RoundDone;

    /// <summary>
    /// Круг сорвался. Отдельно от <see cref="RoundDone"/>, потому что это разные
    /// вещи: «маршруты не ответили» — обычное дело, а «контур не смог сделать
    /// круг» — неисправность, и путать их значит прятать вторую за первой.
    /// </summary>
    public event Action<Exception>? RoundFailed;

    /// <summary>
    /// Класс трафика, которым меряется уверенность. Тот же, что у полосы: показать
    /// человеку одно число, а решение принять по другому — верный способ получить
    /// вопрос «почему выбран не тот».
    /// </summary>
    public CoreSla Sla { get; init; } = CoreSla.Browse;

    /// <summary>
    /// Что сделать после круга — здесь применяют решения ядра.
    ///
    /// <para>Не событие, и это существенно. Применение обращается к тому же
    /// движку, что и замеры, а он не терпит одновременных вызовов: два потока
    /// внутри него дают отказ «занят», а не тихую порчу — но отказ этот срывает
    /// круг. К тому же остановка контура ждёт именно эту задачу, и вынеси
    /// применение в подписчика — оно продолжалось бы уже после того, как память
    /// ядра освободили.</para>
    /// </summary>
    public Func<ProbeRound, CancellationToken, Task>? AfterRound { get; init; }

    private int _networkChanged;

    /// <summary>
    /// Маршруты, чьи адреса молчат из этой сети. Набор заменяется целиком, а не
    /// правится по месту: пишут его с потока окна, читает контур, и замена
    /// ссылки — единственное, что здесь безопасно без замка.
    /// </summary>
    private volatile IReadOnlySet<string> _silent = new HashSet<string>();

    /// <summary>
    /// Отметить маршруты, адреса которых молчат: проверка узлов только что
    /// установила это настоящим протоколом мимо туннеля.
    ///
    /// <para>Такие маршруты не меряются, но и не выпадают из учёта: ядро
    /// получает по ним отказ каждый круг, как если бы мерило само. Просто
    /// перестать о них сообщать нельзя — тогда у маршрута, который ещё недавно
    /// был хорош, осталась бы прежняя оценка, и ядро выбрало бы его по старой
    /// памяти. Перестанет адрес молчать — следующая проверка снимет отметку, и
    /// маршрут вернётся в замеры без переподключения.</para>
    /// </summary>
    public void MarkSilent(IEnumerable<string> tags) => _silent = tags.ToHashSet();

    /// <summary>
    /// Сообщить, что сеть сменилась. Обесценивание накопленного делает сам
    /// контур в начале следующего круга: ядро не терпит одновременных вызовов,
    /// а событие о смене сети приходит с чужого потока в любой момент.
    /// </summary>
    public void NoteNetworkChanged() => Interlocked.Exchange(ref _networkChanged, 1);

    /// <summary>
    /// Крутиться, пока не попросят остановиться. Возврат — только по отмене:
    /// исключения наружу не выпускаем, потому что упавший контур означает
    /// клиент, который тихо перестал следить за маршрутами.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var warmingUp = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var round = await RoundAsync(ct).ConfigureAwait(false);
                warmingUp = round.WarmingUp;

                try { RoundDone?.Invoke(round); }
                catch (Exception) { /* подписчик не должен ронять контур */ }

                if (AfterRound is { } after) await after(round, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Сорванный круг контур не убивает: сеть моргнула — через
                // полминуты попробуем снова. Умерший же контур означает клиент,
                // который тихо перестал следить за маршрутами и не заметит,
                // когда сеть вернётся.
                try { RoundFailed?.Invoke(e); }
                catch (Exception) { /* подписчик не должен ронять контур */ }

                warmingUp = false;
            }

            try
            {
                await Task.Delay(Step(warmingUp), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Один круг: замерить все маршруты и подать замеры ядру.</summary>
    public async Task<ProbeRound> RoundAsync(CancellationToken ct)
    {
        // Сеть сменилась — накопленное обесценивается. Задержки через другой
        // канал с прежними несравнимы, и разность между ними не джиттер, а
        // разные измерения, выданные за одно.
        if (Interlocked.Exchange(ref _networkChanged, 0) == 1)
        {
            _engine.NetworkChanged();

            // Историю обнулили — набирать её надо заново и быстро.
            _warm = true;
        }

        var alive = 0;
        var silent = _silent;

        // Молчащие адреса не меряем: их проба — это таймаут целиком, впустую
        // и в каждом круге. Отказ ядру подаём сразу, без замера.
        foreach (var target in _targets.Where(t => silent.Contains(t.Tag)))
            _engine.Observe(target.Route, CoreOutcome.Timeout, 0, Now());

        foreach (var batch in Batches(silent))
        {
            var probes = await Task.WhenAll(batch.Select(async target =>
                (Target: target, Delay: await _source.DelayAsync(target.Tag, ct).ConfigureAwait(false))))
                .ConfigureAwait(false);

            // Время берём после замеров, но одно на пачку: ядро сравнивает
            // свежесть, и разброс в доли секунды внутри пачки для него шум.
            var now = Now();

            foreach (var (target, delay) in probes)
            {
                if (delay is { } ms)
                {
                    _engine.Observe(target.Route, CoreOutcome.Ok, ms, now);
                    alive++;
                }
                else
                {
                    // Молчание — это отказ, а не отсутствие данных. Пропустить
                    // его значит оставить мёртвый маршрут с прежней хорошей
                    // оценкой: ядро сочло бы его лучшим и поставило бы полосе.
                    _engine.Observe(target.Route, CoreOutcome.Timeout, 0, now);
                }
            }
        }

        var confidence = Confidence();

        // Порог перешагнули — частый опрос выключается и обратно сам не
        // включается. Иначе старение замеров между редкими кругами роняло бы
        // уверенность ниже порога, контур снова частил бы, уверенность росла —
        // и так по кругу, без конца.
        if (_warm && confidence >= WarmEnough) _warm = false;

        return new ProbeRound(alive, _targets.Count, confidence, _warm);
    }

    /// <summary>
    /// Уверенность ядра в самом изученном маршруте. Именно максимум, а не
    /// среднее: мёртвый маршрут уверенности не набирает никогда, и среднее по
    /// каталогу с парой мёртвых узлов не поднялось бы до порога вовсе — клиент
    /// остался бы в разогреве навсегда.
    /// </summary>
    private float Confidence()
    {
        var now = Now();
        var best = 0f;

        foreach (var target in _targets)
        {
            var (_, confidence, _) = _engine.Score(target.Route, Sla, now);
            if (confidence > best) best = confidence;
        }

        return best;
    }

    private IEnumerable<ProbeTarget[]> Batches(IReadOnlySet<string> silent)
    {
        var measured = _targets.Where(t => !silent.Contains(t.Tag)).ToArray();

        for (var i = 0; i < measured.Length; i += AtOnce)
            yield return measured.Skip(i).Take(AtOnce).ToArray();
    }

    /// <summary>
    /// Пауза до следующего круга — с разбросом. Ровный период через все узлы
    /// сразу — это рисунок, по которому клиента узнают, не разбирая содержимое:
    /// одинаковые всплески одинаковой длины через равные промежутки.
    /// </summary>
    private TimeSpan Step(bool warmingUp)
    {
        var basis = warmingUp ? WarmUpStep : SteadyStep;
        var spread = basis.TotalMilliseconds * 0.2;

        return TimeSpan.FromMilliseconds(basis.TotalMilliseconds + _jitter.NextDouble() * spread * 2 - spread);
    }

    /// <summary>
    /// Часы для ядра — от запуска машины, а не от эпохи. Ядро считает по ним
    /// свежесть и старение; настенные часы для этого не годятся, они прыгают
    /// при синхронизации времени и при выходе из сна.
    /// </summary>
    private static ulong Now() => (ulong)Environment.TickCount64;
}
