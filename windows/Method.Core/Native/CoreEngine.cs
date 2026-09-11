// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Runtime.InteropServices;
using System.Text;

namespace Method.Core.Native;

/// <summary>Ось обхода: независимый способ пройти, а не протокол.</summary>
public enum CoreAxis
{
    /// <summary>Hysteria2, TUIC.</summary>
    QuicUdp = 0,

    /// <summary>Reality gRPC.</summary>
    FakeTlsH2 = 1,

    /// <summary>Reality Vision.</summary>
    FakeTlsTcp = 2,

    /// <summary>Trojan.</summary>
    RealTls = 3,

    /// <summary>Shadowsocks-2022.</summary>
    RawStream = 4,

    /// <summary>Прямой выход и блокировка — оси не имеют.</summary>
    None = 5,
}

/// <summary>Куда выходит трафик. Значения совпадают с битами маски разрешений.</summary>
public enum CoreExposure
{
    Tunnelled = 1,
    Direct = 2,
    Blocked = 4,
}

/// <summary>
/// Класс трафика: чем для него оборачивается плохой маршрут.
///
/// <para>От него зависят и веса в оценке, и ворота. Для звонка беда — рывок,
/// для загрузки — узкий канал, а для чувствительного трафика цена ошибки
/// вообще не в неудобстве, а в раскрытии: там неизвестный выход
/// дисквалифицирует маршрут, тогда как для обычного веба он допустим.</para>
/// </summary>
public enum CoreSla
{
    /// <summary>Игры, звонки, удалённый терминал.</summary>
    Realtime = 0,

    /// <summary>Обычный веб.</summary>
    Browse = 1,

    /// <summary>Видео.</summary>
    Stream = 2,

    /// <summary>Загрузки и синхронизация.</summary>
    Bulk = 3,

    /// <summary>Всё, где цена ошибки — раскрытие.</summary>
    Sensitive = 4,
}

/// <summary>Чем кончилась проба.</summary>
public enum CoreOutcome
{
    Ok = 0,
    Timeout = 1,
    Refused = 2,
    Discarded = 3,
    ExitMismatch = 4,
}

/// <summary>Что движок велит сделать.</summary>
public enum CoreActionKind
{
    Select = 0,
    GoEmpty = 1,
    Drain = 2,
}

/// <summary>Маршрут в понимании ядра: узел вместе с транспортом, а не узел.</summary>
public sealed record CoreRoute(
    string Id,
    string Node,
    string Transport,
    string Country,
    CoreAxis Axis,
    CoreExposure Exposure = CoreExposure.Tunnelled);

/// <summary>Полоса: именованный класс трафика с подменяемым набором маршрутов.</summary>
public sealed record CoreLane(string Id, string Title);

/// <summary>Одно действие исполнителю вместе с объяснением по-русски.</summary>
public sealed record CoreAction(CoreActionKind Kind, uint Lane, uint Route, string Reason);

/// <summary>
/// Движок решений общего ядра: какой маршрут в какую полосу поставить сейчас.
///
/// <para>Он ничего не подключает, не измеряет и не рисует. Клиент подаёт ему
/// пробы, спрашивает решение и <b>подтверждает</b> применение: пока
/// подтверждения нет, полоса для движка остаётся без маршрута, и следующий
/// вопрос вернёт тот же ответ. Это нарочно — только исполнитель знает, дошёл
/// ли его вызов до движка sing-box.</para>
///
/// <para>Каталог маршрутов и полос объявляется один раз и удалений не
/// поддерживает: удаление либо сдвинуло бы чужие дескрипторы, либо оставило
/// дыры. Каталог поменялся — создаётся новый движок.</para>
/// </summary>
public sealed partial class CoreEngine : IDisposable
{
    private const string Library = "method_core_ffi";

    private readonly IntPtr _engine;
    private bool _disposed;

    /// <param name="probeInterval">
    /// Шаг измерительного контура. Это не «настройка частоты»: от него зависит
    /// старение уверенности и порог, после которого замер считается протухшим.
    /// </param>
    public CoreEngine(TimeSpan probeInterval)
    {
        var ms = (ulong)Math.Max(1, probeInterval.TotalMilliseconds);

        Check(EngineNew(ms, out _engine));
    }

    private static bool? _available;

    /// <summary>
    /// Есть ли на машине библиотека ядра.
    ///
    /// <para>Узнать иначе нельзя: библиотека подгружается лениво, при первом
    /// же вызове, и её отсутствие или чужая разрядность видны только по
    /// исключению. Ответ запоминается — за время работы файл не появится, а
    /// каждая проверка загружает библиотеку заново.</para>
    ///
    /// <para>«Нет» — не поломка и не повод ругаться в интерфейсе. Туннель
    /// поднимает движок, ядро лишь ведёт выбор узла; без него выбирает сам
    /// движок, как и раньше. Спрашивать надо до сборки конфигурации: от
    /// ответа зависит, соберётся группа переключателем или самоходной.</para>
    /// </summary>
    public static bool Available => _available ??= Probe();

    private static bool Probe()
    {
        try
        {
            using var engine = new CoreEngine(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>Объявить маршрут. Возвращает дескриптор — им же и оперируем дальше.</summary>
    public uint AddRoute(CoreRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Умолчания спрашиваем у ядра: обнулённая структура означала бы «не
        // несёт ни TCP, ни UDP» и «гистерезис ноль», то есть дребезг на каждом
        // замере.
        var desc = new McRouteDesc();
        Check(RouteDescInit(ref desc));

        using var id = Utf8.Rent(route.Id);
        using var node = Utf8.Rent(route.Node);
        using var transport = Utf8.Rent(route.Transport);
        using var country = Utf8.Rent(route.Country);
        using var exposureNode = Utf8.Rent(string.Empty);

        desc.Id = id.Str;
        desc.Node = node.Str;
        desc.Transport = transport.Str;
        desc.Country = country.Str;
        desc.ExposureNode = exposureNode.Str;
        desc.Axis = (int)route.Axis;
        desc.Exposure = (int)route.Exposure;

        Check(EngineAddRoute(_engine, ref desc, out var handle));
        return handle;
    }

    /// <summary>Объявить полосу с заводским гистерезисом.</summary>
    public uint AddLane(CoreLane lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var desc = new McLaneDesc();
        Check(LaneDescInit(ref desc));

        using var id = Utf8.Rent(lane.Id);
        using var title = Utf8.Rent(lane.Title);
        using var justification = Utf8.Rent(string.Empty);
        using var onEmpty = Utf8.Rent(string.Empty);

        desc.Id = id.Str;
        desc.Title = title.Str;
        desc.Justification = justification.Str;
        desc.OnEmptyLane = onEmpty.Str;

        Check(EngineAddLane(_engine, ref desc, out var handle));
        return handle;
    }

    /// <summary>Подать замер задержки.</summary>
    public void Observe(uint route, CoreOutcome outcome, double rttMs, ulong atMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var gotNode = Utf8.Rent(string.Empty);

        var probe = new McProbe
        {
            Route = route,
            Outcome = (int)outcome,
            Cause = 0,
            RttMs = (float)rttMs,
            AtMs = atMs,
            GotNode = gotNode.Str,
        };

        Check(EngineObserve(_engine, ref probe));
    }

    /// <summary>
    /// Сеть сменилась: накопленное обесценивается. Задержки через другой канал
    /// с прежними несравнимы, а разность между ними — не джиттер.
    /// </summary>
    public void NetworkChanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(EngineNetworkChanged(_engine));
    }

    /// <summary>
    /// Свести желаемое с действительным. Возвращает действия и объяснения; сами
    /// действия исполняет вызывающий и подтверждает через <see cref="LaneApplied"/>.
    /// </summary>
    public IReadOnlyList<CoreAction> Reconcile(ulong nowMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Check(EngineReconcile(_engine, nowMs, out var decision));

        try
        {
            Check(DecisionActionCount(decision, out var count));

            var actions = new List<CoreAction>((int)count);

            for (nuint i = 0; i < count; i++)
            {
                Check(DecisionAction(decision, i, out var action));

                actions.Add(new CoreAction(
                    (CoreActionKind)action.Kind, action.Lane, action.Route, Read(action.Reason)));
            }

            return actions;
        }
        finally
        {
            DecisionFree(decision);
        }
    }

    /// <summary>Объяснения решения — включая то, почему движок НЕ стал переключаться.</summary>
    public IReadOnlyList<string> Explain(ulong nowMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Check(EngineReconcile(_engine, nowMs, out var decision));

        try
        {
            Check(DecisionReasonCount(decision, out var count));

            var reasons = new List<string>((int)count);

            for (nuint i = 0; i < count; i++)
            {
                Check(DecisionReason(decision, i, out var reason));
                reasons.Add(Read(reason.Text));
            }

            return reasons;
        }
        finally
        {
            DecisionFree(decision);
        }
    }

    /// <summary>
    /// Оценка маршрута — ровно то же вычисление, каким пользуется контур
    /// решений. Показать человеку число, отличное от того, по которому принято
    /// решение, — верный способ получить вопрос «почему выбран не тот».
    /// </summary>
    public (float Value, float Confidence, uint Gates) Score(uint route, CoreSla sla, ulong nowMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var score = new McScore();
        Check(EngineScore(_engine, route, (int)sla, nowMs, ref score));

        return (score.Value, score.Confidence, score.Gates);
    }

    /// <summary>Подтвердить, что маршрут действительно поставлен полосе.</summary>
    public void LaneApplied(uint lane, uint route)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(EngineLaneApplied(_engine, lane, route));
    }

    /// <summary>Полоса осталась без маршрута: обрыв, блокировка, отказ применения.</summary>
    public void LaneCleared(uint lane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(EngineLaneCleared(_engine, lane));
    }

    /// <summary>Какой маршрут стоит у полосы сейчас. Ноль — никакой.</summary>
    public uint LaneCurrent(uint lane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Check(EngineLaneCurrent(_engine, lane, out var route));
        return route;
    }

    /// <summary>
    /// Закрепить маршрут за полосой: ручной выбор человека сильнее всей
    /// математики. Но не сильнее фактов — мёртвый маршрут движок снимет и
    /// скажет об этом словами.
    /// </summary>
    public void LanePin(uint lane, uint route, ulong untilMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(EngineLanePin(_engine, lane, route, untilMs));
    }

    public void LaneUnpin(uint lane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(EngineLaneUnpin(_engine, lane));
    }

    /// <summary>
    /// Выгрузить историю измерений.
    ///
    /// <para>Только здоровье маршрутов — ни остывание, ни состояние полос ядро
    /// не сохраняет, и это не упущение. То и другое выражено в монотонных часах,
    /// которые при перезапуске начинают счёт заново; восстановленное остывание
    /// из чужой эпохи означало бы либо вечный запрет на переключение, либо
    /// мгновенное разрешение.</para>
    /// </summary>
    public byte[] SaveState(ulong nowMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var buffer = new McBuffer();
        Check(EngineSaveState(_engine, nowMs, ref buffer));

        try
        {
            if (buffer.Data == IntPtr.Zero || buffer.Len == 0) return [];

            var bytes = new byte[checked((int)buffer.Len)];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);

            return bytes;
        }
        finally
        {
            // Буфер выдан во владение и освобождается только этим вызовом:
            // память под него выделяло ядро своим распределителем.
            BufferFree(ref buffer);
        }
    }

    /// <summary>
    /// Загрузить историю измерений.
    ///
    /// <para>Ради этого всё и затевалось: без переноса истории каждый запуск
    /// даёт несколько минут, когда все маршруты одинаково недостоверны и выбор
    /// фактически случаен. С переносом число проб и ширина статистики
    /// сохраняются, и первая же успешная проба возвращает маршруту полную
    /// уверенность вместо шестидесяти проб с нуля.</para>
    ///
    /// <para><paramref name="elapsedMs"/> — сколько <b>настоящего</b> времени
    /// прошло между сохранением и этой загрузкой, по стенным часам. Библиотека
    /// часов не читает принципиально и потому спрашивает: без этого числа замер
    /// прошлой недели выглядел бы свежим.</para>
    /// </summary>
    public void LoadState(ReadOnlySpan<byte> state, ulong nowMs, ulong elapsedMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (state.IsEmpty) return;

        unsafe
        {
            fixed (byte* data = state)
                Check(EngineLoadState(_engine, data, (nuint)state.Length, nowMs, elapsedMs));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        EngineFree(_engine);
    }

    private static string Read(CoreStr str) =>
        str.Ptr == IntPtr.Zero || str.Len == 0
            ? string.Empty
            : Marshal.PtrToStringUTF8(str.Ptr, checked((int)str.Len)) ?? string.Empty;

    private static void Check(int code)
    {
        if (code == (int)McStatus.Ok) return;

        var message = "ядро отказало без объяснения";

        if (LastError(out var text) == (int)McStatus.Ok && text.Ptr != IntPtr.Zero && text.Len > 0)
            message = Marshal.PtrToStringUTF8(text.Ptr, checked((int)text.Len)) ?? message;

        throw new MethodCoreException((McStatus)code, message);
    }

    // MARK: Граница

    [LibraryImport(Library, EntryPoint = "mc_last_error")]
    private static partial int LastError(out CoreStr text);

    [LibraryImport(Library, EntryPoint = "mc_engine_new")]
    private static partial int EngineNew(ulong probeIntervalMs, out IntPtr engine);

    [LibraryImport(Library, EntryPoint = "mc_engine_free")]
    private static partial void EngineFree(IntPtr engine);

    [LibraryImport(Library, EntryPoint = "mc_route_desc_init")]
    private static partial int RouteDescInit(ref McRouteDesc desc);

    [LibraryImport(Library, EntryPoint = "mc_lane_desc_init")]
    private static partial int LaneDescInit(ref McLaneDesc desc);

    [LibraryImport(Library, EntryPoint = "mc_engine_add_route")]
    private static partial int EngineAddRoute(IntPtr engine, ref McRouteDesc desc, out uint route);

    [LibraryImport(Library, EntryPoint = "mc_engine_add_lane")]
    private static partial int EngineAddLane(IntPtr engine, ref McLaneDesc desc, out uint lane);

    [LibraryImport(Library, EntryPoint = "mc_engine_observe")]
    private static partial int EngineObserve(IntPtr engine, ref McProbe probe);

    [LibraryImport(Library, EntryPoint = "mc_engine_network_changed")]
    private static partial int EngineNetworkChanged(IntPtr engine);

    [LibraryImport(Library, EntryPoint = "mc_engine_reconcile")]
    private static partial int EngineReconcile(IntPtr engine, ulong nowMs, out IntPtr decision);

    [LibraryImport(Library, EntryPoint = "mc_decision_action_count")]
    private static partial int DecisionActionCount(IntPtr decision, out nuint count);

    [LibraryImport(Library, EntryPoint = "mc_decision_action")]
    private static partial int DecisionAction(IntPtr decision, nuint index, out McAction action);

    [LibraryImport(Library, EntryPoint = "mc_decision_reason_count")]
    private static partial int DecisionReasonCount(IntPtr decision, out nuint count);

    [LibraryImport(Library, EntryPoint = "mc_decision_reason")]
    private static partial int DecisionReason(IntPtr decision, nuint index, out McReason reason);

    [LibraryImport(Library, EntryPoint = "mc_decision_free")]
    private static partial void DecisionFree(IntPtr decision);

    [LibraryImport(Library, EntryPoint = "mc_engine_score")]
    private static partial int EngineScore(IntPtr engine, uint route, int sla, ulong nowMs, ref McScore score);

    [LibraryImport(Library, EntryPoint = "mc_engine_lane_applied")]
    private static partial int EngineLaneApplied(IntPtr engine, uint lane, uint route);

    [LibraryImport(Library, EntryPoint = "mc_engine_lane_cleared")]
    private static partial int EngineLaneCleared(IntPtr engine, uint lane);

    [LibraryImport(Library, EntryPoint = "mc_engine_lane_current")]
    private static partial int EngineLaneCurrent(IntPtr engine, uint lane, out uint route);

    [LibraryImport(Library, EntryPoint = "mc_engine_lane_pin")]
    private static partial int EngineLanePin(IntPtr engine, uint lane, uint route, ulong untilMs);

    [LibraryImport(Library, EntryPoint = "mc_engine_lane_unpin")]
    private static partial int EngineLaneUnpin(IntPtr engine, uint lane);

    [LibraryImport(Library, EntryPoint = "mc_engine_save_state")]
    private static partial int EngineSaveState(IntPtr engine, ulong nowMs, ref McBuffer buffer);

    [LibraryImport(Library, EntryPoint = "mc_engine_load_state")]
    private static unsafe partial int EngineLoadState(
        IntPtr engine, byte* data, nuint len, ulong nowMs, ulong elapsedMs);

    [LibraryImport(Library, EntryPoint = "mc_buffer_free")]
    private static partial void BufferFree(ref McBuffer buffer);

    /// <summary>Строка, закреплённая на время вызова: ядро её только читает.</summary>
    private readonly struct Utf8 : IDisposable
    {
        private readonly GCHandle _handle;

        public CoreStr Str { get; }

        private Utf8(GCHandle handle, CoreStr str)
        {
            _handle = handle;
            Str = str;
        }

        public static Utf8 Rent(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            return new Utf8(handle, new CoreStr
            {
                Ptr = handle.AddrOfPinnedObject(),
                Len = (nuint)bytes.Length,
            });
        }

        public void Dispose()
        {
            if (_handle.IsAllocated) _handle.Free();
        }
    }
}

/// <summary>
/// Буфер, выданный ядром во владение вызывающему. Освобождается только
/// <c>mc_buffer_free</c> — память под него выделял чужой распределитель, и
/// отдать её нашему сборщику мусора нельзя.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct McBuffer
{
    public IntPtr Data;
    public nuint Len;
}

/// <summary>
/// Строка границы: указатель на UTF-8 и длина. Объявлена рядом со структурами,
/// а не внутри класса: генератор вызовов требует тип, видимый ему целиком.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CoreStr
{
    public IntPtr Ptr;
    public nuint Len;
}

/// <summary>Описание маршрута. Раскладка обязана совпадать с заголовком границы.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct McRouteDesc
{
    public CoreStr Id;
    public CoreStr Node;
    public CoreStr Transport;
    public CoreStr Country;
    public CoreStr ExposureNode;
    public int Axis;
    public int Exposure;
    public int HandshakeCost;
    public byte CarriesTcp;
    public byte CarriesUdp;
    public byte CarriesV4;
    public byte CarriesV6;
}

/// <summary>Описание полосы.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct McLaneDesc
{
    public CoreStr Id;
    public CoreStr Title;
    public CoreStr Justification;
    public CoreStr OnEmptyLane;

    public IntPtr AxisIn;
    public nuint AxisInLen;
    public IntPtr AxisNotIn;
    public nuint AxisNotInLen;
    public IntPtr IncludeCountry;
    public nuint IncludeCountryLen;
    public IntPtr ExcludeCountry;
    public nuint ExcludeCountryLen;

    public ulong CooldownMs;
    public ulong CooldownMaxMs;
    public float MarginFloor;
    public int Sla;
    public int OnEmpty;
    public int SwitchMode;
    public byte Allow;
    public byte Dwell;
    public byte MinAxes;
    public byte RequireUdp;
    public byte RequireV6;
}

/// <summary>Оценка маршрута под класс нагрузки.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct McScore
{
    public float Value;
    public float BandLo;
    public float BandHi;
    public float Confidence;
    public int Limiter;
    public int Display;
    public uint Gates;
    public byte HasBand;
}

/// <summary>Проба.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct McProbe
{
    public uint Route;
    public int Outcome;
    public int Cause;
    public float RttMs;
    public ulong AtMs;
    public CoreStr GotNode;
}

/// <summary>Действие исполнителю.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct McAction
{
    public int Kind;
    public uint Lane;
    public uint Route;
    public int OnEmpty;
    public uint OnEmptyLane;
    public int ReasonKind;
    public CoreStr Reason;
}

/// <summary>Запись журнала причин.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct McReason
{
    public int Kind;
    public uint Lane;
    public CoreStr Text;
}
