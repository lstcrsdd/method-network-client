// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Method.App.Services;
using Method.Core.Core;
using Method.Core.Models;
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

        RebuildGroups();
        SelectedServer = Servers.FirstOrDefault();
        StartAutoUpdate();
        StartWatchdog();

        // Состояние спрашиваем сразу и в двух смыслах: есть ли привилегированная
        // часть и не поднят ли туннель ещё до нас. Второе важнее: она живёт
        // отдельно от окна, и окно, открытое поверх работающего туннеля, обязано
        // это увидеть — иначе оно показывает «не подключено» и не даёт ни
        // отключить, ни подключиться.
        Dispatcher.UIThread.Post(async () =>
        {
            await CheckHostAsync().ConfigureAwait(true);
            await AdoptRunningTunnelAsync().ConfigureAwait(true);
        });
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
    }

    // MARK: Состояние подключения

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
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

    /// <summary>
    /// Цвет точки состояния. Красный только при настоящем отказе: серым «не
    /// подключено» человек не тревожится, а красным — тревожится, и повод должен быть.
    /// </summary>
    public IBrush StatusBrush => State switch
    {
        ConnectionState.Connected => ConnectedBrush,
        ConnectionState.Connecting => WarningBrush,
        ConnectionState.Failed => ErrorBrush,
        _ => MutedBrush,
    };

    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#29C940"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#FFBD2E"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF5E57"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#FFFFFF"), 0.28);

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

    // MARK: Подключение

    [RelayCommand]
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
            return;
        }

        State = ConnectionState.Connecting;
        MarkTunnelAction();
        Detail = null;

        string config;
        try
        {
            config = SingBoxConfigBuilder.BuildJson(item.Profile, new SingBoxConfigBuilder.Options
            {
                // Секрет свой на каждое подключение: зашитый в исходник секрет —
                // это отсутствие секрета, а управлять движком по этому адресу может
                // любой процесс на машине.
                ClashSecret = NewSecret(),
            });
        }
        catch (Exception e)
        {
            State = ConnectionState.Failed;
            Detail = $"Конфигурацию собрать не удалось: {e.Message}";
            return;
        }

        var (ok, error, warning) = await TunnelClient
            .StartAsync(config, KillSwitchEnabled, BlockLanDns)
            .ConfigureAwait(true);

        if (!ok)
        {
            State = ConnectionState.Failed;
            Detail = error ?? "причина неизвестна";
            return;
        }

        State = ConnectionState.Connected;
        _connectedAt = DateTimeOffset.Now;
        UpdateElapsed();
        _sessionTimer.Start();

        // Предупреждение показываем именно как предупреждение: туннель поднят, но
        // часть защиты не применилась. Промолчать здесь — значит нарисовать защиту,
        // которой нет.
        Detail = warning;

        // Внешний адрес спрашиваем уже внутри туннеля: это единственное прямое
        // доказательство, что трафик пошёл через него.
        await RefreshExternalIpAsync().ConfigureAwait(true);
    }

    private async Task DisconnectAsync()
    {
        _sessionTimer.Stop();
        MarkTunnelAction();
        Elapsed = "00:00";
        ExternalIp = "—";

        var (ok, error, warning) = await TunnelClient.StopAsync().ConfigureAwait(true);

        State = ConnectionState.Disconnected;
        Detail = ok ? warning : error;
    }

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
    /// Замерить задержку до всех узлов разом. Замеры идут параллельно: последовательно
    /// список из пятнадцати серверов обновлялся бы полминуты, и человек всё это время
    /// смотрел бы на пустые строки.
    /// </summary>
    [RelayCommand]
    private async Task MeasurePingsAsync()
    {
        var items = Servers.ToArray();
        if (items.Length == 0) return;

        foreach (var item in items) item.IsMeasuring = true;

        await Task.WhenAll(items.Select(async item =>
        {
            var ms = await PingService.MeasureAsync(item.Profile.Host, item.Profile.Port).ConfigureAwait(true);
            item.Ping = ms;
            item.IsMeasuring = false;
            item.MarkMeasured();
        })).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Remove(ServerItem? item)
    {
        if (item is null) return;

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
