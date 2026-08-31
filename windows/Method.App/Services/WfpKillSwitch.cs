// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.Ndis;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;

namespace Method.App.Services;

/// <summary>
/// Блокировка утечки на платформе фильтрации Windows.
///
/// <para><b>Почему не правилами брандмауэра.</b> Прежняя блокировка переключала
/// политику всей машины: «запретить исходящее по умолчанию» на всех профилях.
/// У этого две беды, обе видели вживую. Первая — общий выключатель: два клиента
/// с такой блокировкой не уживаются, второй ломает первого, и оба уверены, что
/// правы. Вторая — блокировка переживала падение процесса и оставляла машину
/// без сети до ручной уборки.</para>
///
/// <para><b>Что даёт платформа фильтрации.</b> Свой подслой с собственными
/// фильтрами: политику машины не трогаем, чужие правила не переписываем.
/// А главное — сеанс объявлен динамическим, и система снимает все объекты
/// сеанса сама, как только дескриптор закрыт или процесс исчез. Блокировка
/// физически не может пережить того, кто её поставил.</para>
///
/// <para><b>Что разрешено.</b> Сам движок — он держит туннель к серверу через
/// физический адаптер. Адаптер туннеля — через него идут приложения. Петля,
/// локальная подсеть и получение адреса. Всё остальное закрыто, пока блокировка
/// включена: упал движок — прямой путь остаётся закрыт, и мимо туннеля не
/// уходит ни трафик, ни имена.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WfpKillSwitch : IDisposable
{
    /// <summary>
    /// Свои опознавательные знаки. Постоянные: по ним видно наши объекты в
    /// системных средствах, и по ним же их можно убрать, если что-то осталось
    /// от аварийной сборки.
    /// </summary>
    private static readonly Guid ProviderKey = new("6b8b4567-1f2e-4b3a-9c5d-methodvpn01".Replace("methodvpn", "8f1c2d3e"));

    private static readonly Guid SubLayerKey = new("6b8b4567-1f2e-4b3a-9c5d-8f1c2d3e0002");

    /// <summary>
    /// Вес подслоя. Берём высокий, но не предельный: предельный отбирает
    /// последнее слово у средств защиты, которым его отдавать не наше дело.
    /// </summary>
    private const ushort SubLayerWeight = 0x8000;

    /// <summary>Вес запрещающего фильтра — самый низкий: его перебивают разрешения.</summary>
    private const ulong BlockWeight = 1;

    /// <summary>Вес разрешающих фильтров. Выше запрета, ниже потолка.</summary>
    private const ulong AllowWeight = 10;

    /// <summary>
    /// Действия фильтра. Оба «завершающие»: подошло — решение принято, дальше
    /// по цепочке не идём. Незавершающее разрешение означало бы «не возражаю»,
    /// и последнее слово осталось бы за чужим фильтром.
    /// </summary>
    private const uint ActionBlock = 0x00000001 | 0x00001000;
    private const uint ActionPermit = 0x00000002 | 0x00001000;

    /// <summary>
    /// Дескриптор подсистемы. Обёрнут, потому что генератор границы ждёт
    /// именно защищённый дескриптор: он гарантирует, что закрытие произойдёт
    /// ровно один раз, даже если снятие блокировки и уборка сойдутся вместе.
    /// </summary>
    private sealed class EngineHandle(IntPtr handle) : SafeHandle(handle, ownsHandle: true)
    {
        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => PInvoke.FwpmEngineClose0(this) == 0;
    }

    private EngineHandle? _engine;
    private bool _disposed;

    public string? LastError { get; private set; }

    public bool Active => _engine is { IsInvalid: false };

    /// <summary>
    /// Включить блокировку. Возвращает <c>false</c> и заполняет
    /// <see cref="LastError"/>, если не вышло, — молчаливый отказ здесь означал
    /// бы «защита есть», когда её нет.
    /// </summary>
    public unsafe bool Enable(string enginePath, string tunnelInterfaceName, bool allowLan)
    {
        Disable();

        try
        {
            if (!Open("Method: блокировка утечки")) return false;
            if (!AddProvider()) return false;
            if (!AddSubLayer()) return false;

            // Порядок здесь роли не играет: пока не добавлен запрет, ничего не
            // блокируется, а фильтры внутри подслоя разбираются по весу, а не по
            // очерёдности добавления.
            if (!AllowEngine(enginePath)) return false;
            if (!AllowInterface(tunnelInterfaceName)) return false;
            if (!AllowLoopback()) return false;
            if (allowLan && !AllowPrivateNetworks()) return false;
            if (!AllowDhcp()) return false;
            if (!BlockEverythingElse()) return false;

            LastError = null;
            return true;
        }
        catch (Exception e)
        {
            Disable();
            LastError = $"блокировка не поставлена: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// Закрыть шестую версию протокола целиком, кроме движка и петли.
    ///
    /// <para>Нужно даже без общей блокировки: адреса шестой версии туннелю мы
    /// не выдаём, и система вольна пустить такой трафик мимо него. Снаружи это
    /// выглядит идеально — «подключено», адрес сменился, — а часть соединений
    /// всё это время идёт открытым путём.</para>
    /// </summary>
    public unsafe bool EnableIpv6Guard(string enginePath)
    {
        Disable();

        if (!Open("Method: шестая версия закрыта")) return false;
        if (!AddProvider() || !AddSubLayer()) return false;

        // Движку можно: сервер может быть доступен по шестой версии, и запрет
        // без этого исключения закрыл бы сам туннель.
        if (!AllowEngine(enginePath)) return false;
        if (!AllowLoopback()) return false;

        return AddFilter("Шестая версия закрыта", permit: false, BlockWeight, null, 0, ipv4: false);
    }

    /// <summary>
    /// Закрыть обращения к именам в локальную сеть.
    ///
    /// <para>Домашний маршрутизатор — обычный резольвер, и система охотно
    /// спрашивает его напрямую. Запрос уходит мимо туннеля, и провайдер видит,
    /// какие имена спрашивают, даже когда сам трафик защищён.</para>
    /// </summary>
    public unsafe bool EnableLanDnsGuard()
    {
        Disable();

        if (!Open("Method: имена мимо туннеля закрыты")) return false;
        if (!AddProvider() || !AddSubLayer()) return false;

        (uint Addr, uint Mask)[] ranges =
        [
            (0x0A000000, 0xFF000000),
            (0xAC100000, 0xFFF00000),
            (0xC0A80000, 0xFFFF0000),
        ];

        foreach (var (addr, mask) in ranges)
        {
            var network = new FWP_V4_ADDR_AND_MASK { addr = addr, mask = mask };
            var port = (ushort)53;

            var conditions = stackalloc FWPM_FILTER_CONDITION0[2];

            conditions[0] = new FWPM_FILTER_CONDITION0
            {
                fieldKey = PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS,
                matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE0
                {
                    type = FWP_DATA_TYPE.FWP_V4_ADDR_MASK,
                    Anonymous = { v4AddrMask = &network },
                },
            };

            conditions[1] = new FWPM_FILTER_CONDITION0
            {
                fieldKey = PInvoke.FWPM_CONDITION_IP_REMOTE_PORT,
                matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE0
                {
                    type = FWP_DATA_TYPE.FWP_UINT16,
                    Anonymous = { uint16 = port },
                },
            };

            if (!AddFilter("Имена в локальную сеть", permit: false, AllowWeight, conditions, 2, ipv6: false))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Снять блокировку. Достаточно закрыть сеанс: объекты динамического сеанса
    /// система убирает сама, перечислять и удалять их по одному не нужно.
    /// </summary>
    public void Disable()
    {
        if (_engine is null) return;

        // Закрытие идёт через защищённый дескриптор: он сам зовёт закрытие
        // подсистемы ровно один раз, сколько бы раз ни позвали нас.
        _engine.Dispose();
        _engine = null;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Disable();
    }

    // ── Объекты сеанса ──

    /// <summary>
    /// Открыть динамический сеанс. Динамический — ключевое слово: система
    /// снимает все его объекты сама, когда дескриптор закрыт или процесс исчез.
    /// Именно этим новая блокировка отличается от прежней, которая переживала
    /// падение и оставляла машину без сети.
    /// </summary>
    private unsafe bool Open(string title)
    {
        var session = new FWPM_SESSION0
        {
            flags = PInvoke.FWPM_SESSION_FLAG_DYNAMIC,
            displayData = Display(title, "Снимается вместе с процессом"),
        };

        HANDLE opened;
        var code = PInvoke.FwpmEngineOpen0(null, 10 /* RPC_C_AUTHN_WINNT */, null, &session, &opened);

        if (code != 0) return Fail("не удалось открыть подсистему фильтрации", code);

        _engine = new EngineHandle(opened);
        return true;
    }

    private unsafe bool AddProvider()
    {
        fixed (char* name = "Method")
        fixed (char* note = "Клиент Method")
        {
            var provider = new FWPM_PROVIDER0
            {
                providerKey = ProviderKey,
                displayData = new FWPM_DISPLAY_DATA0 { name = name, description = note },
            };

            var code = PInvoke.FwpmProviderAdd0(_engine!, in provider, default);

            // Провайдер уже есть — это не беда: он мог остаться от прошлого
            // сеанса, потому что провайдер живёт дольше фильтров.
            return code == 0 || code == 0x80320009 /* FWP_E_ALREADY_EXISTS */
                || Fail("не удалось объявить провайдера", code);
        }
    }

    private unsafe bool AddSubLayer()
    {
        fixed (char* name = "Method: блокировка утечки")
        {
            var layer = new FWPM_SUBLAYER0
            {
                subLayerKey = SubLayerKey,
                displayData = new FWPM_DISPLAY_DATA0 { name = name },
                weight = SubLayerWeight,
            };

            var code = PInvoke.FwpmSubLayerAdd0(_engine!, in layer, default);

            return code == 0 || code == 0x80320009 || Fail("не удалось объявить подслой", code);
        }
    }

    // ── Фильтры ──

    /// <summary>
    /// Движку — можно. Он единственный, кому нужен прямой выход наружу: через
    /// физический адаптер он держит соединение с сервером, и без этого правила
    /// блокировка закрыла бы сам туннель.
    /// </summary>
    private unsafe bool AllowEngine(string enginePath)
    {
        FWP_BYTE_BLOB* appId = null;

        fixed (char* path = enginePath)
        {
            var code = PInvoke.FwpmGetAppIdFromFileName0(path, &appId);
            if (code != 0) return Fail($"движок не опознан по пути «{enginePath}»", code);
        }

        try
        {
            var condition = new FWPM_FILTER_CONDITION0
            {
                fieldKey = PInvoke.FWPM_CONDITION_ALE_APP_ID,
                matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE0
                {
                    type = FWP_DATA_TYPE.FWP_BYTE_BLOB_TYPE,
                    Anonymous = { byteBlob = appId },
                },
            };

            return AddFilter("Движок", permit: true, AllowWeight, &condition, 1);
        }
        finally
        {
            if (appId != null) PInvoke.FwpmFreeMemory0((void**)&appId);
        }
    }

    /// <summary>
    /// Адаптеру туннеля — можно: через него идёт трафик приложений. Ищем по
    /// идентификатору интерфейса, а не по имени: имя человек волен поменять, а
    /// фильтр обязан пережить переименование.
    /// </summary>
    private unsafe bool AllowInterface(string interfaceName)
    {
        if (!TryFindLuid(interfaceName, out var luid))
        {
            // Адаптера ещё нет — это не отказ: он появляется, когда движок
            // поднимет туннель, а блокировка ставится раньше.
            return true;
        }

        var value = luid;

        var condition = new FWPM_FILTER_CONDITION0
        {
            fieldKey = PInvoke.FWPM_CONDITION_IP_LOCAL_INTERFACE,
            matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
            conditionValue = new FWP_CONDITION_VALUE0
            {
                type = FWP_DATA_TYPE.FWP_UINT64,
                Anonymous = { uint64 = &value },
            },
        };

        return AddFilter("Туннель", permit: true, AllowWeight, &condition, 1);
    }

    /// <summary>Петля: без неё встанет всё, что общается само с собой на машине.</summary>
    private unsafe bool AllowLoopback()
    {
        var v4 = new FWP_V4_ADDR_AND_MASK { addr = 0x7F000000, mask = 0xFF000000 };

        var condition = new FWPM_FILTER_CONDITION0
        {
            fieldKey = PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS,
            matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
            conditionValue = new FWP_CONDITION_VALUE0
            {
                type = FWP_DATA_TYPE.FWP_V4_ADDR_MASK,
                Anonymous = { v4AddrMask = &v4 },
            },
        };

        return AddFilter("Петля", permit: true, AllowWeight, &condition, 1);
    }

    /// <summary>
    /// Локальная сеть: принтеры, хранилища, шлюз. Отключаемо — кому нужна
    /// герметичность, тот закрывает и это.
    /// </summary>
    private unsafe bool AllowPrivateNetworks()
    {
        (uint Addr, uint Mask)[] ranges =
        [
            (0x0A000000, 0xFF000000),   // 10/8
            (0xAC100000, 0xFFF00000),   // 172.16/12
            (0xC0A80000, 0xFFFF0000),   // 192.168/16
            (0xA9FE0000, 0xFFFF0000),   // 169.254/16 — адрес без DHCP
        ];

        foreach (var (addr, mask) in ranges)
        {
            var v4 = new FWP_V4_ADDR_AND_MASK { addr = addr, mask = mask };

            var condition = new FWPM_FILTER_CONDITION0
            {
                fieldKey = PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS,
                matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE0
                {
                    type = FWP_DATA_TYPE.FWP_V4_ADDR_MASK,
                    Anonymous = { v4AddrMask = &v4 },
                },
            };

            if (!AddFilter("Локальная сеть", permit: true, AllowWeight, &condition, 1)) return false;
        }

        return true;
    }

    /// <summary>
    /// Получение адреса. Без него машина, включённая с уже поднятой блокировкой,
    /// не получит адрес по DHCP и останется без сети вообще — включая туннель.
    /// </summary>
    private unsafe bool AllowDhcp()
    {
        var protocol = (byte)17;    // UDP
        var port = (ushort)67;      // сервер DHCP

        var conditions = stackalloc FWPM_FILTER_CONDITION0[2];

        conditions[0] = new FWPM_FILTER_CONDITION0
        {
            fieldKey = PInvoke.FWPM_CONDITION_IP_PROTOCOL,
            matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
            conditionValue = new FWP_CONDITION_VALUE0
            {
                type = FWP_DATA_TYPE.FWP_UINT8,
                Anonymous = { uint8 = protocol },
            },
        };

        conditions[1] = new FWPM_FILTER_CONDITION0
        {
            fieldKey = PInvoke.FWPM_CONDITION_IP_REMOTE_PORT,
            matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
            conditionValue = new FWP_CONDITION_VALUE0
            {
                type = FWP_DATA_TYPE.FWP_UINT16,
                Anonymous = { uint16 = port },
            },
        };

        return AddFilter("Получение адреса", permit: true, AllowWeight, conditions, 2);
    }

    /// <summary>Всё остальное — закрыто. Условий нет: фильтр ловит любое соединение.</summary>
    private unsafe bool BlockEverythingElse() =>
        AddFilter("Прочее закрыто", permit: false, BlockWeight, null, 0);

    /// <summary>
    /// Один фильтр в оба слоя — для IPv4 и для IPv6. Забыть про шестую версию
    /// значит оставить открытой дыру ровно там, где её никто не проверяет.
    /// </summary>
    private unsafe bool AddFilter(
        string name, bool permit, ulong weight, FWPM_FILTER_CONDITION0* conditions, uint count,
        bool ipv4 = true, bool ipv6 = true)
    {
        var layers = new List<Guid>(2);
        if (ipv4) layers.Add(PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4);
        if (ipv6) layers.Add(PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6);

        foreach (var layer in layers)
        {
            var weightValue = weight;

            fixed (char* title = $"Method: {name}")
            {
                var filter = new FWPM_FILTER0
                {
                    displayData = new FWPM_DISPLAY_DATA0 { name = title },
                    providerKey = (Guid*)Unsafe(ProviderKey),
                    layerKey = layer,
                    subLayerKey = SubLayerKey,
                    weight = new FWP_VALUE0
                    {
                        type = FWP_DATA_TYPE.FWP_UINT64,
                        Anonymous = { uint64 = &weightValue },
                    },
                    numFilterConditions = count,
                    filterCondition = conditions,
                    Anonymous = default,
                    action = new FWPM_ACTION0 { type = (FWP_ACTION_TYPE)(permit ? ActionPermit : ActionBlock) },
                };

                var code = PInvoke.FwpmFilterAdd0(_engine!, in filter, default, null);
                if (code != 0) return Fail($"фильтр «{name}» не добавлен", code);
            }
        }

        return true;
    }

    // ── Мелочи ──

    /// <summary>
    /// Идентификатор адаптера по имени. Перебираем список системы: имя,
    /// показанное человеку, и системное имя интерфейса — разные строки, и
    /// совпасть может любая из них.
    /// </summary>
    private static bool TryFindLuid(string name, out ulong luid)
    {
        luid = 0;

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            var matches =
                string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains(name, StringComparison.OrdinalIgnoreCase);

            if (!matches) continue;

            var index = adapter.Supports(NetworkInterfaceComponent.IPv4)
                ? adapter.GetIPProperties().GetIPv4Properties()?.Index
                : null;

            if (index is null) continue;

            unsafe
            {
                NET_LUID_LH value;
                if (PInvoke.ConvertInterfaceIndexToLuid((uint)index.Value, &value) != 0) continue;

                luid = value.Value;
                return true;
            }
        }

        return false;
    }

    private static unsafe FWPM_DISPLAY_DATA0 Display(string name, string description)
    {
        // Строки живут в закреплённой памяти до конца вызова, которому они
        // отданы: подсистема копирует их себе, но делает это внутри вызова.
        fixed (char* n = name)
        fixed (char* d = description)
        {
            return new FWPM_DISPLAY_DATA0 { name = n, description = d };
        }
    }

    private static unsafe void* Unsafe(in Guid value)
    {
        fixed (Guid* p = &value) return p;
    }

    private bool Fail(string what, uint code)
    {
        LastError = $"{what} (код 0x{code:X8})";
        Disable();
        return false;
    }
}
