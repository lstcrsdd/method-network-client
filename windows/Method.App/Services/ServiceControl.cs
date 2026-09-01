// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Windows.Win32;
using Windows.Win32.System.Services;

namespace Method.App.Services;

/// <summary>
/// Запись службы в базе служб Windows: создать, обновить, снять, спросить состояние.
///
/// <para>Регистрация идёт напрямую через Win32, а не через <c>sc.exe</c>. Причина
/// не в изяществе: путь установки содержит пробел («Program Files»), а команда
/// <c>sc create</c> принимает путь и аргументы одной строкой, которую сама же
/// разбирает по своим правилам. Кавычки внутри кавычек внутри аргумента — это
/// способ зарегистрировать службу, которая выглядит установленной и не
/// запускается. Через Win32 путь передаётся строкой, и разбирать его некому.</para>
///
/// <para>Управление состоянием (пуск, остановка, опрос) — наоборот, через
/// готовый <see cref="ServiceController"/>: там нечего выигрывать.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class ServiceControl
{
    /// <summary>Имя в базе служб. По нему её найдут в <c>services.msc</c> и в <c>sc query</c>.</summary>
    public const string ServiceName = "MethodTunnel";

    private const string DisplayName = "Method — туннель";

    private const string Description =
        "Привилегированная часть клиента Method: поднимает туннель, держит сетевой адаптер, " +
        "правит маршруты и блокировку утечки. Без неё клиент не подключается.";

    /// <summary>Состояние службы глазами системы.</summary>
    public enum State
    {
        /// <summary>Службы нет вовсе.</summary>
        Missing,

        /// <summary>Служба зарегистрирована, но не выполняется.</summary>
        Stopped,

        /// <summary>Служба выполняется.</summary>
        Running,

        /// <summary>Есть, но состояние спросить не удалось — обычно не хватило прав.</summary>
        Unknown,
    }

    // Права доступа. Просим ровно то, что нужно каждому шагу: открытая на
    // SERVICE_ALL_ACCESS ручка в коде установки — это не удобство, а привычка,
    // которая однажды выдаст лишнее.
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStop = 0x0020;
    private const uint Delete = 0x0001_0000;

    // Оставить поле конфигурации как есть.
    private const uint NoChange = 0xFFFF_FFFF;

    /// <summary>SERVICE_SID_TYPE_UNRESTRICTED — служба получает собственный SID в токене.</summary>
    private const uint ServiceSidTypeUnrestricted = 1;

    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceMarkedForDelete = 1072;

    /// <summary>Состояние службы. Ошибку прав от отсутствия отличает.</summary>
    public static State Query()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running or ServiceControllerStatus.StartPending => State.Running,
                _ => State.Stopped,
            };
        }
        catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            // 1060 — «указанная служба не установлена». Единственный код, по
            // которому «нет» означает именно «нет».
            return State.Missing;
        }
        catch (Exception)
        {
            return State.Unknown;
        }
    }

    /// <summary>
    /// Зарегистрировать службу или обновить путь у существующей.
    ///
    /// <para>Существующая именно обновляется, а не пересоздаётся. Удаление службы
    /// в Windows отложенное: пока открыта хоть одна ручка или служба ещё не
    /// остановилась, запись помечается к удалению, и следующее создание падает с
    /// «служба помечена для удаления» — до перезагрузки. Переустановка поверх
    /// работающей версии наступала бы на это каждый раз.</para>
    /// </summary>
    /// <returns>Причина отказа или <c>null</c>, если получилось.</returns>
    public static string? Register(string exePath, Action<string> log)
    {
        // Путь берётся в кавычки: без них система читает «C:\Program» как имя
        // файла, а «Files\Method\Method.exe» — как первый аргумент.
        var binaryPath = $"\"{exePath}\" --service";

        using var manager = PInvoke.OpenSCManager(
            (string?)null, (string?)null, ScManagerConnect | ScManagerCreateService);

        if (manager is null || manager.IsInvalid)
            return $"база служб недоступна: {LastError()}";

        using var created = PInvoke.CreateService(
            manager,
            ServiceName,
            DisplayName,
            ServiceChangeConfig | ServiceQueryStatus | ServiceStop,
            ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS,
            SERVICE_START_TYPE.SERVICE_AUTO_START,
            SERVICE_ERROR.SERVICE_ERROR_NORMAL,
            binaryPath,
            lpLoadOrderGroup: null,
            lpdwTagId: null,
            // Зависимостей нет намеренно. Просилась «базовая фильтрация» (BFE) —
            // на ней стоит блокировка утечки. Но зависимость означает: не
            // поднялась она — не поднимаемся и мы, то есть клиент не работает
            // вовсе. Блокировка нужна в момент подключения, через минуты после
            // загрузки, и проверить фильтрацию мы успеем тогда же.
            lpDependencies: null,
            // Пусто — значит LocalSystem. Пароля у него нет.
            lpServiceStartName: null,
            lpPassword: null);

        if (created is not null && !created.IsInvalid)
        {
            log($"служба {ServiceName} создана");
            return Configure(created, log);
        }

        var error = Marshal.GetLastWin32Error();

        if (error == ErrorServiceMarkedForDelete)
            return "прежняя служба помечена к удалению и исчезнет только после перезагрузки. " +
                   "Перезагрузите машину и повторите установку";

        if (error != ErrorServiceExists)
            return $"создать службу не удалось: {LastError(error)}";

        // Служба уже есть — обновляем путь. Он мог измениться: например, сборка
        // переехала из отладочного каталога в Program Files.
        using var existing = PInvoke.OpenService(
            manager, ServiceName, ServiceChangeConfig | ServiceQueryStatus | ServiceStop);

        if (existing is null || existing.IsInvalid)
            return $"служба есть, но открыть её не удалось: {LastError()}";

        // Запуск при загрузке выставляется заново, а не оставляется как есть.
        // Установка перед этим службу выключала (см. <see cref="SetStartType"/>),
        // и не вернуть её сюда значило бы оставить человека с записью, которая
        // никогда не поднимется. Заодно чинится служба, выключенная руками или
        // чистильщиком автозагрузки.
        var updated = PInvoke.ChangeServiceConfig(
            existing,
            (ENUM_SERVICE_TYPE)NoChange,
            SERVICE_START_TYPE.SERVICE_AUTO_START,
            (SERVICE_ERROR)NoChange,
            binaryPath,
            lpLoadOrderGroup: null,
            lpdwTagId: null,
            lpDependencies: null,
            lpServiceStartName: null,
            lpPassword: null,
            lpDisplayName: DisplayName);

        if (!updated) return $"обновить путь службы не удалось: {LastError()}";

        log($"служба {ServiceName} уже была, путь обновлён");
        return Configure(existing, log);
    }

    /// <summary>
    /// Выключить службу на время установки.
    ///
    /// <para>Нужно из-за действий при падении, которые мы же и настроили. Если
    /// служба не остановилась по-хорошему и её приходится гасить убийством
    /// процесса, система расценит это как падение и поднимет её через пять
    /// секунд — прямо посреди копирования файлов, которые она держит. Выключенную
    /// службу система не поднимает никакими действиями. Запуск при загрузке
    /// возвращает <see cref="Register"/>, и он же возвращает его при неудачной
    /// установке — иначе выключенная запись пережила бы отказ.</para>
    /// </summary>
    public static void SetStartType(bool enabled, Action<string> log)
    {
        using var manager = PInvoke.OpenSCManager((string?)null, (string?)null, ScManagerConnect);
        if (manager is null || manager.IsInvalid) return;

        using var service = PInvoke.OpenService(manager, ServiceName, ServiceChangeConfig);

        // Службы нет — и выключать нечего. Это обычный случай первой установки.
        if (service is null || service.IsInvalid) return;

        var startType = enabled ? SERVICE_START_TYPE.SERVICE_AUTO_START : SERVICE_START_TYPE.SERVICE_DISABLED;

        var changed = PInvoke.ChangeServiceConfig(
            service,
            (ENUM_SERVICE_TYPE)NoChange,
            startType,
            (SERVICE_ERROR)NoChange,
            lpBinaryPathName: null,
            lpLoadOrderGroup: null,
            lpdwTagId: null,
            lpDependencies: null,
            lpServiceStartName: null,
            lpPassword: null,
            lpDisplayName: null);

        if (!changed) log($"запуск службы при загрузке не переключён: {LastError()}");
    }

    /// <summary>
    /// Довесок к записи службы: описание, поведение при падении, собственный SID.
    ///
    /// <para>Ни одно из этого не задаётся при создании — только отдельными
    /// вызовами. Неудача любого из них не отменяет службу: она уже создана и
    /// работать будет, просто без описания или без перезапуска после падения.
    /// Поэтому здесь мы говорим в журнал, а не возвращаем отказ.</para>
    /// </summary>
    private static string? Configure(SafeHandle service, Action<string> log)
    {
        fixed (char* description = Description)
        {
            var info = new SERVICE_DESCRIPTIONW { lpDescription = description };
            if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_DESCRIPTION, &info))
                log($"описание службы не задано: {LastError()}");
        }

        // Перезапуск после падения. Пять секунд, десять, тридцать — и счётчик
        // сбрасывается через сутки. Такой перезапуск бьётся с нашим же надзором
        // за движком: движок поднимает надзор внутри процесса, а сам процесс —
        // система. Второй уровень нужен на случай, в котором первый бессилен:
        // когда падает не движок, а мы.
        var actions = stackalloc SC_ACTION[3];
        actions[0] = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 5_000 };
        actions[1] = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 10_000 };
        actions[2] = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 30_000 };

        var failure = new SERVICE_FAILURE_ACTIONSW
        {
            dwResetPeriod = 86_400,
            cActions = 3,
            lpsaActions = actions,
        };

        if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_FAILURE_ACTIONS, &failure))
            log($"перезапуск после падения не настроен: {LastError()}");

        // Собственный SID службы. Прямо сейчас он не нужен, но с ним появляется
        // возможность выдавать права именно этой службе, а не всему SYSTEM, —
        // например на канал. Ставится один раз при регистрации, задним числом
        // это потребовало бы переустановки.
        var sid = new SERVICE_SID_INFO { dwServiceSidType = ServiceSidTypeUnrestricted };

        if (!PInvoke.ChangeServiceConfig2W(service, SERVICE_CONFIG.SERVICE_CONFIG_SERVICE_SID_INFO, &sid))
            log($"собственный SID службе не выдан: {LastError()}");

        return null;
    }

    /// <summary>Запустить и дождаться состояния «выполняется».</summary>
    public static string? Start(TimeSpan timeout)
    {
        try
        {
            using var controller = new ServiceController(ServiceName);

            if (controller.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
                controller.Start();

            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return null;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return "служба не перешла в состояние «выполняется» за отведённое время";
        }
        catch (Exception e)
        {
            return $"запустить службу не удалось: {e.Message}";
        }
    }

    /// <summary>
    /// Остановить и дождаться. Отсутствие службы отказом не считается: тот, кто
    /// просит остановить, хочет, чтобы её не было, — и её нет.
    /// </summary>
    public static string? Stop(TimeSpan timeout)
    {
        try
        {
            using var controller = new ServiceController(ServiceName);

            if (controller.Status is ServiceControllerStatus.Stopped) return null;

            if (controller.Status is not ServiceControllerStatus.StopPending) controller.Stop();

            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            return null;
        }
        catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            return null;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return "служба не остановилась за отведённое время";
        }
        catch (Exception e)
        {
            return $"остановить службу не удалось: {e.Message}";
        }
    }

    /// <summary>Снять запись о службе. Останавливать перед этим — забота вызывающего.</summary>
    public static string? Remove(Action<string> log)
    {
        using var manager = PInvoke.OpenSCManager((string?)null, (string?)null, ScManagerConnect);

        if (manager is null || manager.IsInvalid)
            return $"база служб недоступна: {LastError()}";

        using var service = PInvoke.OpenService(manager, ServiceName, Delete);

        if (service is null || service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();

            // 1060 — службы нет. Удалять нечего, и это успех, а не отказ.
            if (error == 1060) return null;

            return $"открыть службу для удаления не удалось: {LastError(error)}";
        }

        if (!PInvoke.DeleteService(service))
            return $"удалить службу не удалось: {LastError()}";

        log($"служба {ServiceName} удалена");
        return null;
    }

    private static string LastError() => LastError(Marshal.GetLastWin32Error());

    private static string LastError(int code) => $"{new Win32Exception(code).Message} (код {code})";
}
