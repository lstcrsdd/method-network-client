// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Method.Core.Tunnel;

/// <summary>
/// Страховка от осиротевшего sing-box — объект задания Windows с флагом
/// «гасить процессы при закрытии».
///
/// <para><b>Что лечит.</b> Привилегированная часть запускает sing-box дочерним
/// процессом и надзирает за ним. Но если она сама умирает нештатно — падение,
/// <c>taskkill</c>, аварийное завершение сеанса, — обработчики выхода уже не
/// отработают: sing-box остаётся жив, адаптер поднят, весь трафик машины идёт
/// через туннель, которым никто не управляет. Интерфейс при этом показывает
/// «отключено» и остановить туннель не может. Со стороны это выглядит как
/// «интернет сломался», и чинится поиском процесса руками.</para>
///
/// <para><b>Как работает.</b> Задание создаётся один раз и держится открытым всю
/// жизнь процесса. Каждый запущенный sing-box к нему привязывается. Windows
/// закрывает задание, когда закрывается последний дескриптор — то есть в момент
/// смерти владельца, штатной или нет, — и с этим флагом гасит все процессы
/// задания. Сирот не остаётся by design, без единой строчки в обработчиках.</para>
///
/// <para><b>Разделение ролей</b> (не перепутать при правках): задание отвечает
/// только за сирот при смерти владельца. Перезапуск упавшего sing-box — забота
/// надзора. Пока владелец жив, задание ни во что не вмешивается.</para>
///
/// <para><b>Остаточная гонка</b> осознанная: привязка идёт сразу после старта, и
/// теоретически процесс успел бы породить потомка вне задания. Убрать окно
/// полностью можно только создав процесс приостановленным, минуя обычный запуск, —
/// это заметно сложнее и рискованнее. Для sing-box гонка непрактична: потомков на
/// старте он не порождает.</para>
///
/// Исключений не бросает никогда: неудача привязки не должна ронять владельца —
/// туннель важнее страховки. Все сбои уходят в журнал вызывающего.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessJobGuard
{
    private static readonly object Lock = new();
    private static IntPtr _job = IntPtr.Zero;
    private static bool _createAttempted;

    /// <summary>Задание создано и готово принимать процессы.</summary>
    public static bool IsActive
    {
        get { lock (Lock) return _job != IntPtr.Zero; }
    }

    /// <summary>
    /// Создаёт задание один раз за жизнь процесса. Идемпотентно, потокобезопасно,
    /// не бросает. Дескриптор намеренно не закрывается: его закрытие и есть сигнал
    /// «гасить потомков», поэтому жить он должен ровно столько же, сколько владелец.
    /// </summary>
    public static bool EnsureCreated(Action<string>? log = null)
    {
        lock (Lock)
        {
            if (_job != IntPtr.Zero) return true;
            // Уже пробовали и не вышло — не долбимся на каждый старт.
            if (_createAttempted) return false;
            _createAttempted = true;

            try
            {
                // Безымянное задание: имя не нужно, а именованное можно было бы перехватить.
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    log?.Invoke($"Объект задания: создать не удалось (Win32 {Marshal.GetLastWin32Error()})");
                    return false;
                }

                var info = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };

                var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
                    if (!SetInformationJobObject(job, ExtendedLimitInformationClass, ptr, (uint)size))
                    {
                        log?.Invoke($"Объект задания: настроить не удалось (Win32 {Marshal.GetLastWin32Error()})");
                        CloseHandle(job);
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                _job = job;
                log?.Invoke("Объект задания создан: осиротевший sing-box будет погашен вместе с владельцем");
                return true;
            }
            catch (Exception e)
            {
                log?.Invoke($"Объект задания: создать не удалось ({e.GetType().Name}: {e.Message})");
                return false;
            }
        }
    }

    /// <summary>
    /// Привязывает процесс к заданию. Ложь означает «страховки нет» — вызывающий
    /// продолжает работу, но обязан это записать в журнал.
    /// </summary>
    public static bool TryAssign(Process process, Action<string>? log = null)
    {
        if (process is null) return false;

        lock (Lock)
        {
            if (_job == IntPtr.Zero) return false;

            try
            {
                if (process.HasExited)
                {
                    log?.Invoke("Объект задания: процесс завершился до привязки");
                    return false;
                }

                if (AssignProcessToJobObject(_job, process.Handle)) return true;

                log?.Invoke(
                    $"Объект задания: привязать процесс не удалось (Win32 {Marshal.GetLastWin32Error()}) — " +
                    "sing-box работает без страховки");
                return false;
            }
            catch (Exception e)
            {
                log?.Invoke($"Объект задания: привязка не удалась ({e.GetType().Name}: {e.Message})");
                return false;
            }
        }
    }

    // MARK: Win32

    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
