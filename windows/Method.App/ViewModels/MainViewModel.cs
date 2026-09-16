// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Method.App.Services;
using Method.Core;
using Method.Core.Core;
using Method.Core.Models;
using Method.Core.Native;
using Method.Core.Services;
using Method.Core.Storage;
using Method.Core.Subscriptions;

namespace Method.App.ViewModels;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

/// <summary>Разделы в боковой панели.</summary>
public enum AppPage
{
    Overview,
    Servers,
    Scenarios,
    Settings,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly SubscriptionService _subscriptions;
    private readonly DispatcherTimer _sessionTimer;
    private DateTimeOffset _connectedAt;

    public MainViewModel() : this(
        ProfileStore.Default(new DpapiSecretBox()),
        new SubscriptionService(new HttpClient(), new WindowsDeviceIdentity()))
    {
    }

    public MainViewModel(ProfileStore store, SubscriptionService subscriptions)
    {
        _store = store;
        _subscriptions = subscriptions;
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sessionTimer.Tick += (_, _) => UpdateElapsed();

        foreach (var stored in _store.LoadProfiles()) Servers.Add(new ServerItem(stored));
        foreach (var subscription in _store.LoadSubscriptions())
            Subscriptions.Add(new SubscriptionItem(subscription));

        var settings = LoadSettings();
        LoadRules(settings.Rules);
        RebuildGroups();

        // График заводим сразу ровной линией по нулю: пустой он схлопывает
        // карточку в первый же кадр, а линия появляется только при подключении —
        // ровно тот скачок вёрстки, ради которого карточку и оставили на месте.
        FillFlat();

        // Возвращаем тот узел, что был выбран в прошлый раз: клиент, каждый раз
        // сбрасывающий выбор на первый попавшийся, заставляет выбирать заново.
        SelectedServer = Servers.FirstOrDefault(s => s.Profile.Id == settings.SelectedServer)
                         ?? Servers.FirstOrDefault();
        StartAutoUpdate();
        StartWatchdog();

        // Сторож живости спрашивает, жив ли движок; этот — доходит ли
        // трафик наружу. Второе из первого не следует: движок бывает жив при
        // мёртвом узле, и тогда клиент показывает «Подключено» при отсутствии
        // сети.
        StartReachWatch();

        // Заставка показывает эти же шаги человеку. Записи идут после того, как
        // шаг сделан, а не вместо него: журнал докладывает о работе, а не
        // изображает её.
        ReportBoot();

        // Состояние спрашиваем сразу и в двух смыслах: есть ли привилегированная
        // часть и не поднят ли туннель ещё до нас. Второе важнее: она живёт
        // отдельно от окна, и окно, открытое поверх работающего туннеля, обязано
        // это увидеть — иначе оно показывает «не подключено» и не даёт ни
        // отключить, ни подключиться.
        Dispatcher.UIThread.Post(async () =>
        {
            await CheckHostAsync().ConfigureAwait(true);

            WorkJournal.Resolve("Служба", Host switch
            {
                HostState.Ready => "отвечает",
                HostState.Installed => "стоит, но молчит",
                _ => "не установлена",
            }, Host switch
            {
                HostState.Ready => JournalOutcome.Ok,
                HostState.Installed => JournalOutcome.Failed,
                _ => JournalOutcome.Warning,
            });

            await AdoptRunningTunnelAsync().ConfigureAwait(true);

            WorkJournal.Resolve("Туннель",
                IsConnected ? "поднят до нас, подхвачен" : "не поднят",
                IsConnected ? JournalOutcome.Ok : JournalOutcome.Idle);

            WorkJournal.Finish();
        });
    }

    /// <summary>
    /// Доложить журналу, чем кончился запуск.
    ///
    /// <para>Числа здесь настоящие и берутся из того, что уже загружено. Шаги,
    /// ответ на которые придёт позже — служба и туннель, — заводятся сразу со
    /// строкой ожидания: иначе они появлялись бы в списке не на своём месте, и
    /// порядок проверок читался бы задом наперёд.</para>
    /// </summary>
    private void ReportBoot()
    {
        WorkJournal.Add("Хранилище", Servers.Count > 0
            ? $"расшифровано · узлов {Servers.Count}"
            : "пусто · узлов нет",
            Servers.Count > 0 ? JournalOutcome.Ok : JournalOutcome.Warning);

        WorkJournal.Add("Подписки", Subscriptions.Count > 0
            ? $"{Subscriptions.Count} · обновление по расписанию"
            : "нет",
            JournalOutcome.Ok);

        WorkJournal.Add("Правила маршрутизации", Rules.Count > 0 ? $"своих {Rules.Count}" : "только свои умолчания");

        WorkJournal.Add("Защита", Describe());

        WorkJournal.Add("Узел", SelectedServer is null
            ? "не выбран"
            : SelectedServer.Name,
            SelectedServer is null ? JournalOutcome.Warning : JournalOutcome.Ok);

        // Движок ищется в трёх местах — установленный, рядом с клиентом, в
        // исходниках, — и без него туннель не поднять. Молчать об этом нельзя:
        // человек узнавал бы о пропаже только при попытке подключиться.
        var engine = AppPaths.ResolveEngine();
        WorkJournal.Add("Ядро", File.Exists(engine) ? "sing-box на месте" : "не найдено",
            File.Exists(engine) ? JournalOutcome.Ok : JournalOutcome.Failed);

        WorkJournal.Add("Служба", "опрос канала", JournalOutcome.Waiting);
        WorkJournal.Add("Туннель", "проверка", JournalOutcome.Waiting);
    }

    /// <summary>Что из защиты включено — словами, а не тремя галочками.</summary>
    private string Describe()
    {
        var on = new List<string>(3);

        if (KillSwitchEnabled) on.Add("блокировка мимо туннеля");
        if (BlockLanDns) on.Add("запрет DNS в локальную сеть");
        if (AutoReconnect) on.Add("автоподключение");

        return on.Count == 0 ? "всё отключено" : string.Join(" · ", on);
    }

    // MARK: Разделы

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview))]
    [NotifyPropertyChangedFor(nameof(IsServers))]
    [NotifyPropertyChangedFor(nameof(IsScenarios))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    private AppPage _page = AppPage.Overview;

    public bool IsOverview => Page == AppPage.Overview;
    public bool IsServers => Page == AppPage.Servers;
    public bool IsScenarios => Page == AppPage.Scenarios;
    public bool IsSettings => Page == AppPage.Settings;

    /// <summary>
    /// Переход по разделам. Имя строкой, а не значением перечисления: разметка
    /// передаёт параметр как есть, и приведение типа на её стороне — лишний способ
    /// получить неработающую кнопку без единой ошибки при сборке.
    /// </summary>
    [RelayCommand]
    private void GoTo(string? page)
    {
        if (Enum.TryParse<AppPage>(page, out var value)) Page = value;
    }

    // MARK: Серверы

    public ObservableCollection<ServerItem> Servers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtocolName))]
    [NotifyPropertyChangedFor(nameof(SelectedName))]
    private ServerItem? _selectedServer;

    public bool HasServers => Servers.Count > 0;

    public bool IsEmpty => Servers.Count == 0;

    public string SelectedName => SelectedServer?.Name ?? "Сервер не выбран";

    public string ProtocolName => SelectedServer?.Profile.Protocol.DisplayName() ?? "—";

    /// <summary>
    /// Отметка выбранного узла живёт на самой строке: список рисуется обычным
    /// перечислением, а не списком с выделением, поэтому подсветку выбора никто
    /// кроме нас не поставит.
    /// </summary>
    partial void OnSelectedServerChanged(ServerItem? oldValue, ServerItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        SaveSettings();
    }

    // MARK: Состояние подключения

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>Пояснение под кнопкой: ошибка, предупреждение или подсказка.</summary>
    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private string _elapsed = "00:00";

    /// <summary>Внешний адрес после подключения. Прочерк, пока не знаем.</summary>
    [ObservableProperty]
    private string _externalIp = "—";

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsBusy => State == ConnectionState.Connecting;

    /// <summary>Настоящий отказ, а не просто «не подключено».</summary>
    public bool IsFailed => State == ConnectionState.Failed;

    public string StatusText => State switch
    {
        ConnectionState.Disconnected => "Не подключено",
        ConnectionState.Connecting => "Подключение…",
        ConnectionState.Connected => "Подключено",
        ConnectionState.Failed => "Ошибка",
        _ => "",
    };

    public string ActionText => State switch
    {
        ConnectionState.Connected => "ОТКЛЮЧИТЬ",
        ConnectionState.Connecting => "ОТМЕНА",
        _ => "ПОДКЛЮЧИТЬ",
    };

    // Цвета состояния здесь не хранятся намеренно: оформлений два, и кисть,
    // созданная в коде, знает только про одно из них. Модель отдаёт состояние
    // (подключено, идёт, отказ), цвет ему назначает стиль по ключу из набора —
    // и подмена набора перекрашивает точку сама. Правило общее для всего
    // приложения: цвет живёт в стилях, состояние — здесь.

    // MARK: Настройки

    /// <summary>
    /// Kill switch. Настоящий: значение уходит привилегированной части при запуске
    /// туннеля. Тумблеров, которые ничего не делают, здесь нет намеренно — защита,
    /// включённая на вид и выключенная на деле, хуже выключенной честно.
    /// </summary>
    [ObservableProperty]
    private bool _killSwitchEnabled;

    /// <summary>Запретить запросы DNS в локальную сеть. Тоже настоящий.</summary>
    [ObservableProperty]
    private bool _blockLanDns;

    /// <summary>
    /// Клиент сам выбирает узел для всего, что не описано сценариями. Сценарии
    /// при этом главнее: они перекрывают автоматику точечно, а не отменяют её.
    /// </summary>
    [ObservableProperty]
    private bool _autoRouting;


    // MARK: Подключение

    /// <summary>
    /// Подключить или отключить — смотря по состоянию.
    ///
    /// <para>Команде разрешено запускаться поверх себя, и это не небрежность.
    /// По умолчанию такая команда на время работы объявляет себя недоступной, а
    /// кнопка при этом гаснет до служебного серого — и ровно в те секунды, пока
    /// на ней написано «ОТМЕНА». Нажать её в этот момент было нельзя: клиент
    /// предлагал отмену и сам же её не принимал.</para>
    ///
    /// <para>Двойного пуска отсюда не выйдет: первым делом смотрится состояние,
    /// и повторное нажатие во время подключения уводит в отключение, а не
    /// запускает вторую попытку.</para>
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleAsync()
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            await DisconnectAsync().ConfigureAwait(true);
            return;
        }

        await ConnectAsync().ConfigureAwait(true);
    }

    private async Task ConnectAsync()
    {
        var item = SelectedServer;
        if (item is null)
        {
            Page = AppPage.Servers;
            Detail = "Сначала добавьте сервер по ссылке.";
            WorkJournal.Add("Пуск", "узел не выбран", JournalOutcome.Failed);
            return;
        }

        // Два туннеля на одной машине — это не два туннеля, а ни одного: оба
        // забирают маршрут по умолчанию и перехватывают DNS, и новые соединения
        // рвутся, пока старые живут. Подключаться поверх чужого VPN значит
        // сломать человеку связь и выглядеть при этом виновником.
        if (ForeignTunnel.Find() is { } foreign)
        {
            Detail = $"Уже работает другой VPN — «{foreign}». Два туннеля делят один маршрут, и сеть " +
                     "начинает рваться. Выключите его режим TUN и подключитесь снова.";
            WorkJournal.Add("Пуск", $"отказ: поднят чужой туннель «{foreign}»", JournalOutcome.Failed);
            return;
        }

        State = ConnectionState.Connecting;
        MarkTunnelAction();
        Detail = null;

        // Время пуска считаем от нажатия и до внешнего адреса: это ровно то, что
        // человек ощущает как «сколько оно поднимается».
        var started = Stopwatch.StartNew();
        WorkJournal.Add("Пуск", item.Name, JournalOutcome.Waiting);

        // Итог пуска дописывается отдельной строкой в конце, а не подставляется
        // в эту: лента читается сверху вниз как хроника, и «готов за 8,4 с»,
        // стоящее выше собственных шагов, читается задом наперёд.

        // Секрет нужен и движку в конфигурации, и нам — чтобы потом спрашивать у
        // него статистику. Поэтому он рождается здесь и живёт до отключения.
        var secret = NewSecret();

        // Порт управления ищем свободный. Привычные 9090 держит любой другой
        // sing-box на машине — и тогда движок падает на старте, не поднимая
        // туннель вовсе.
        var controlPort = LocalPort.Pick();

        // Узлы полос собираем до сборки конфигурации: они нужны и ей, и журналу.
        var lanes = LaneNodes(item);

        // Настройки сборки держим отдельной переменной: по ним же потом узнаётся
        // состав группы автоматики. Собери его контур сам — правило жило бы в
        // двух местах, и замеры однажды легли бы не на те маршруты.
        var options = new SingBoxConfigBuilder.Options
        {
            // Секрет свой на каждое подключение: зашитый в исходник секрет —
            // это отсутствие секрета, а управлять движком по этому адресу может
            // любой процесс на машине.
            ClashSecret = secret,

            // Адрес управления — тот, что мы только что заняли проверкой.
            ClashController = $"127.0.0.1:{controlPort}",

            // Правила человека уходят в движок вместе с конфигурацией: менять
            // их на лету движок не даёт, поэтому они применяются с этого
            // подключения и до отключения.
            Rules = Rules.Select(r => r.Rule).ToArray(),

            // Узлы, названные правилами, поднимаются вместе с основным.
            LaneNodes = lanes,

            // Автоматика — фон: выбирает узел для всего, что правилами не
            // описано. Выбирать ей есть из чего только при нескольких узлах.
            Autopilot = AutoRouting,
            // Узлы, не прошедшие проверку, в выбор не берутся: автоматика узнала
            // бы об этом сама, но через минуты неудачных проб, а человек в это
            // время сидел бы без сети. Непроверенные — берутся: незнание не довод.
            AutoNodes = AutoRouting
                ? Servers.Where(s => s.Reachable != false).Select(s => s.Profile).ToArray()
                : [],


            // Есть ядро — группа собирается переключателем, и решения
            // применяются поворотом ручки, без переподключения. Нет ядра —
            // остаётся собственный выбор движка: клиент обязан работать и
            // без библиотеки.
            CoreDriven = CoreEngine.Available,
        };

        string config;
        try
        {
            config = SingBoxConfigBuilder.BuildJson(item.Profile, options);
        }
        catch (Exception e)
        {
            State = ConnectionState.Failed;
            Detail = $"Конфигурацию собрать не удалось: {e.Message}";
            WorkJournal.Add("Конфигурация", e.Message, JournalOutcome.Failed);
            WorkJournal.Resolve("Пуск", item.Name, JournalOutcome.Failed);
            WorkJournal.Add("Итог", "пуск не вышел", JournalOutcome.Failed);
            return;
        }

        // Ни конфигурацию, ни секрет управления в журнал не пишем: в первой лежат
        // пароли узла, второй открывает управление движком. Журнал видно на экране
        // и он же уходит в снимок экрана.
        WorkJournal.Add("Конфигурация", $"{item.Profile.Protocol.DisplayName()} · правил {Rules.Count}");

        // Полосы поднимаются вместе с основным узлом, и человек должен видеть,
        // что их подняли: иначе «через Финляндию» в сценариях выглядит
        // обещанием, которое неизвестно, выполнено ли.
        if (lanes.Count > 0)
            WorkJournal.Add("Полосы", lanes.Count == 1
                ? $"1 узел · {lanes[0].Name}"
                : $"узлов {lanes.Count}");

        if (AutoRouting)
            WorkJournal.Add("Автоматика", Servers.Count > 1
                ? $"выбор из {Servers.Count} узлов"
                : "выбирать не из чего",
                Servers.Count > 1 ? JournalOutcome.Ok : JournalOutcome.Warning);
        WorkJournal.Add("Туннель", "служба поднимает", JournalOutcome.Waiting);

        var (ok, error, warning) = await TunnelClient
            .StartAsync(config, KillSwitchEnabled, BlockLanDns)
            .ConfigureAwait(true);

        if (!ok)
        {
            State = ConnectionState.Failed;
            Detail = error ?? "причина неизвестна";
            WorkJournal.Resolve("Туннель", error ?? "причина неизвестна", JournalOutcome.Failed);
            WorkJournal.Resolve("Пуск", item.Name, JournalOutcome.Failed);
            WorkJournal.Add("Итог", $"не вышло за {Seconds(started)}", JournalOutcome.Failed);
            return;
        }

        WorkJournal.Resolve("Туннель", "поднят", JournalOutcome.Ok);

        State = ConnectionState.Connected;
        _connectedAt = DateTimeOffset.Now;
        UpdateElapsed();
        _sessionTimer.Start();
        StartTraffic(secret, controlPort);

        // Контур замеров заводим сразу за туннелем: ядро набирает уверенность
        // десятками проб, и каждая минута без замеров — это минута, в которую
        // выбор маршрута нечем обосновать.
        if (_clash is not null) StartAutopilot(item.Profile, options, _clash);

        // Предупреждение показываем именно как предупреждение: туннель поднят, но
        // часть защиты не применилась. Промолчать здесь — значит нарисовать защиту,
        // которой нет.
        Detail = warning;

        if (warning is not null) WorkJournal.Add("Защита", warning, JournalOutcome.Warning);
        else if (KillSwitchEnabled) WorkJournal.Add("Защита", "блокировка мимо туннеля включена");

        // Внешний адрес спрашиваем уже внутри туннеля: это единственное прямое
        // доказательство, что трафик пошёл через него.
        WorkJournal.Add("Внешний адрес", "запрос через туннель", JournalOutcome.Waiting);
        await RefreshExternalIpAsync().ConfigureAwait(true);

        var known = ExternalIp is not (null or "—" or "");
        WorkJournal.Resolve("Внешний адрес", known ? ExternalIp! : "не ответил",
            known ? JournalOutcome.Ok : JournalOutcome.Warning);

        WorkJournal.Resolve("Пуск", item.Name, JournalOutcome.Ok);
        WorkJournal.Add("Итог", $"готов за {Seconds(started)}", JournalOutcome.Ok);
    }

    private async Task DisconnectAsync()
    {
        _sessionTimer.Stop();
        MarkTunnelAction();
        Elapsed = "00:00";
        StopTraffic();
        await StopAutopilotAsync().ConfigureAwait(true);
        ExternalIp = "—";

        WorkJournal.Add("Останов", "служба снимает туннель", JournalOutcome.Waiting);

        var (ok, error, warning) = await TunnelClient.StopAsync().ConfigureAwait(true);

        State = ConnectionState.Disconnected;
        Detail = ok ? warning : error;

        WorkJournal.Resolve("Останов",
            ok ? warning ?? "туннель снят, маршруты возвращены" : error ?? "причина неизвестна",
            ok ? warning is null ? JournalOutcome.Ok : JournalOutcome.Warning : JournalOutcome.Failed);
    }

    /// <summary>
    /// Узлы, на которые ссылаются правила полос.
    ///
    /// <para>Берём только названные и только существующие: узел могли удалить
    /// после того, как правило написали. Основной узел из списка исключаем — он
    /// и так поднимается, а второй раз только занял бы соединение впустую.</para>
    /// </summary>
    private IReadOnlyList<ServerProfile> LaneNodes(ServerItem main)
    {
        var wanted = Rules
            .Where(r => r.Rule is { Enabled: true, Aim: RouteAim.Node, Node: not null })
            .Select(r => r.Rule.Node!.Value)
            .Where(id => id != main.Profile.Id)
            .ToHashSet();

        if (wanted.Count == 0) return [];

        return Servers
            .Where(s => wanted.Contains(s.Profile.Id))
            .Select(s => s.Profile)
            .ToArray();
    }

    /// <summary>Длительность для журнала: десятых долей хватает, дальше — шум.</summary>
    private static string Seconds(Stopwatch watch) =>
        $"{watch.Elapsed.TotalSeconds:0.0} с";


    private void UpdateElapsed()
    {
        var span = DateTimeOffset.Now - _connectedAt;
        Elapsed = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    // MARK: Список серверов

    /// <summary>
    /// Добавить серверы из вставленного текста. Возвращает, сколько добавилось и
    /// что не разобралось: строку, которую не приняли, обязаны назвать. Молча взять
    /// девять ссылок из десяти — значит оставить человека выяснять, какой сервер
    /// пропал, уже на попытке подключиться.
    /// </summary>
    private ImportResult AddLinks(string text)
    {
        var (profiles, errors) = ShareLinkParser.ParseMany(text);

        var added = 0;
        foreach (var profile in profiles)
        {
            // Тот же узел дважды в списке не нужен: адрес, порт и протокол
            // определяют его однозначно, а имя провайдер меняет как хочет.
            if (Servers.Any(s => IsSameNode(s.Profile, profile))) continue;

            Servers.Add(new ServerItem(new StoredProfile { Profile = profile }));
            added++;
        }

        if (added > 0)
        {
            Save();
            RebuildGroups();
            SelectedServer ??= Servers.FirstOrDefault();
        }

        NotifyListChanged();
        return new ImportResult(added, profiles.Count - added, errors);
    }

    [RelayCommand]
    private void Select(ServerItem? item)
    {
        if (item is null) return;
        SelectedServer = item;
    }

    /// <summary>
    /// Сколько пробных движков работает разом. Каждый — отдельный процесс со своими
    /// десятками мегабайт; два десятка одновременно заметно нагружают машину, а по
    /// одному проверка пула затягивается на минуту.
    /// </summary>
    private const int ProbesAtOnce = 4;

    /// <summary>
    /// Проверить все узлы делом — запросом через каждый, мимо любого туннеля.
    ///
    /// <para>Раньше здесь мерилось рукопожатие TCP, и оно врало: при поднятом
    /// туннеле его завершает локальный стек за ноль миллисекунд, поэтому живыми
    /// выглядели и адреса, заблокированные по дороге. См. <see cref="NodeProbe"/>.</para>
    ///
    /// <para>После проверки узлы разбираются по адресам: молчит весь адрес — это
    /// блокировка адреса, и такие узлы уходят из автоматического выбора.</para>
    /// </summary>
    [RelayCommand]
    private async Task MeasurePingsAsync()
    {
        var items = Servers.ToArray();
        if (items.Length == 0) return;

        var engine = AppPaths.ResolveEngine();
        var iface = PhysicalInterface.Name();

        foreach (var item in items)
        {
            item.IsMeasuring = true;
            item.Ping = null;
            item.Reachable = null;
            item.AddressSilent = false;
        }

        WorkJournal.Add("Проверка узлов", iface is null
            ? $"узлов {items.Length}; выход в сеть не определён — замер может идти через туннель"
            : $"узлов {items.Length}, мимо туннеля через «{iface}»",
            iface is null ? JournalOutcome.Warning : JournalOutcome.Waiting);

        using var gate = new SemaphoreSlim(ProbesAtOnce);

        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync().ConfigureAwait(true);
            try
            {
                var result = await NodeProbe.CheckAsync(item.Profile, engine, iface).ConfigureAwait(true);
                item.Ping = result.Milliseconds;
                item.Reachable = result.Reachable;
            }
            finally
            {
                item.IsMeasuring = false;
                item.MarkMeasured();
                gate.Release();
            }
        })).ConfigureAwait(true);

        var silent = AddressVerdict.Silent(items.Select(i => (i.Profile.Host, i.Reachable)));
        foreach (var item in items) item.AddressSilent = silent.Contains(item.Profile.Host);

        var alive = items.Count(i => i.Reachable == true);
        WorkJournal.Resolve("Проверка узлов", $"проходят {alive} из {items.Length}",
            alive == 0 ? JournalOutcome.Failed : alive < items.Length ? JournalOutcome.Warning : JournalOutcome.Ok);

        if (silent.Count > 0)
            WorkJournal.Add("Адреса", $"молчат из этой сети: {silent.Count} — узлы на них убраны из автоматического выбора",
                JournalOutcome.Warning);
    }

    /// <summary>
    /// Спросить подтверждение необратимого действия. Задаёт окно; без окна (превью,
    /// проверки) удаление проходит сразу — спрашивать там некого.
    /// </summary>
    public Func<string, string, string, Task<bool>> ConfirmAsync { get; set; } =
        static (_, _, _) => Task.FromResult(true);

    [RelayCommand]
    private async Task RemoveAsync(ServerItem? item)
    {
        if (item is null) return;

        var fromSubscription = item.Stored.SubscriptionId is not null;
        if (!await ConfirmAsync(
                "Удалить сервер?",
                fromSubscription
                    ? $"«{item.Name}» пропадёт из списка. Он пришёл из подписки и вернётся при её следующем обновлении."
                    : $"«{item.Name}» пропадёт из списка. Вернуть его можно только заново добавив ссылку.",
                "Удалить").ConfigureAwait(true))
            return;

        Servers.Remove(item);
        if (ReferenceEquals(SelectedServer, item)) SelectedServer = Servers.FirstOrDefault();

        Save();
        RebuildGroups();
        NotifyListChanged();
    }

    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Save() => _store.SaveProfiles(Servers.Select(s => s.Stored));

    private static bool IsSameNode(ServerProfile a, ServerProfile b) =>
        a.Protocol == b.Protocol
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}

/// <param name="Added">Сколько узлов добавилось.</param>
/// <param name="Duplicates">Сколько разобралось, но уже было в списке.</param>
/// <param name="Errors">Строки, которые разобрать не вышло.</param>
public sealed record ImportResult(int Added, int Duplicates, IReadOnlyList<ShareLinkLineError> Errors)
{
    /// <summary>Подписки, которые не загрузились. Причина у каждой своя и называется целиком.</summary>
    public IReadOnlyList<string> SubscriptionErrors { get; init; } = [];
}
