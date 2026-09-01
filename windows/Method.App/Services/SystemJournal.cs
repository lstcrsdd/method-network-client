// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace Method.App.Services;

/// <summary>
/// Системный журнал Windows — события, о которых должен узнать не разработчик,
/// а хозяин машины.
///
/// <para>Свой журнал в <c>ProgramData</c> остаётся и остаётся подробным: там
/// каждая попытка перезапуска, каждый ответ движка и всё, что нужно при разборе.
/// Но его не видит ни одно штатное средство Windows. Человек, у которого «вчера
/// вечером отвалился интернет», открывает просмотр событий, а не наш файл, — и
/// не находит там ничего, потому что мы туда не писали.</para>
///
/// <para>Поэтому сюда попадают только переломы: туннель поднят, туннель
/// остановлен, движок упал, трафик заблокирован защитой, блокировку не удалось
/// снять. Каждая попытка перезапуска здесь была бы шумом, из-за которого
/// перестают читать и важное.</para>
///
/// <para>Запись сюда не может быть причиной отказа: журнал может быть переполнен,
/// источник — не зарегистрирован, права — урезаны. Всё это молча проглатывается,
/// потому что несделанная запись в журнал никогда не важнее работающего туннеля.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemJournal
{
    /// <summary>Имя источника. Под ним события видны в просмотре событий Windows.</summary>
    private const string Source = "Method";

    private const string LogName = "Application";

    /// <summary>Номера событий. Постоянные: по ним настраивают отбор и оповещения.</summary>
    public static class Id
    {
        public const int TunnelUp = 1;
        public const int TunnelDown = 2;
        public const int EngineDied = 3;
        public const int TrafficBlocked = 4;
        public const int BlockStuck = 5;
        public const int HostFailed = 9;
    }

    /// <summary>
    /// Зарегистрировать источник. Делается при установке, под правами
    /// администратора: обычному пользователю регистрация запрещена, а
    /// привилегированная часть под SYSTEM хоть и может, но лучше ей заниматься
    /// туннелем.
    /// </summary>
    public static void EnsureSource(Action<string> log)
    {
        try
        {
            if (EventLog.SourceExists(Source)) return;

            EventLog.CreateEventSource(new EventSourceCreationData(Source, LogName));
            log($"источник «{Source}» в системном журнале зарегистрирован");
        }
        catch (Exception e)
        {
            log($"источник в системном журнале не зарегистрирован: {e.Message}");
        }
    }

    /// <summary>Снять регистрацию источника при удалении клиента.</summary>
    public static void RemoveSource(Action<string> log)
    {
        try
        {
            if (!EventLog.SourceExists(Source)) return;

            EventLog.DeleteEventSource(Source);
            log($"источник «{Source}» в системном журнале снят");
        }
        catch (Exception e)
        {
            log($"источник в системном журнале не снят: {e.Message}");
        }
    }

    public static void Info(int id, string message) => Write(id, message, EventLogEntryType.Information);

    public static void Warn(int id, string message) => Write(id, message, EventLogEntryType.Warning);

    public static void Error(int id, string message) => Write(id, message, EventLogEntryType.Error);

    private static void Write(int id, string message, EventLogEntryType type)
    {
        try
        {
            EventLog.WriteEntry(Source, message, type, id);
        }
        catch
        {
            // Молча. Смотри объяснение в описании класса: несделанная запись в
            // журнал не повод ронять то, о чём она была.
        }
    }
}
