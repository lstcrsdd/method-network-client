// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Method.App.Services;

/// <summary>Прописанный в Windows прокси и то, отвечает ли он.</summary>
public sealed record SystemProxyState(string Host, int Port, bool Alive)
{
    public string Address => $"{Host}:{Port}";
}

/// <summary>
/// Системный прокси Windows — настройка «Прокси-сервер» текущего пользователя.
///
/// <para><b>Зачем.</b> Method прокси не ставит и не использует: весь трафик идёт
/// через туннель. Но клиенты семейства v2rayN — Happ, NekoRay, сам v2rayN —
/// прописывают туда свой локальный порт, обычно <c>127.0.0.1:10809</c>, и не всегда
/// убирают его за собой. Пока запись жива, браузеры и большая часть программ
/// ходят не через туннель, а в этот порт. Чужой клиент работает — трафик идёт
/// мимо нас. Выключен — порт мёртв, и «интернета нет» при полностью исправном
/// туннеле. Мессенджеры со своими настройками сети при этом работают, отчего
/// поломка выглядит поломкой туннеля.</para>
///
/// <para><b>Снимаем только по нажатию.</b> Отличить рабочий прокси (корпоративный,
/// нужный человеку) от огрызка чужого VPN нельзя, поэтому решает человек. И
/// снимаем только флаг включения: адрес остаётся на месте, вернуть его — одна
/// галочка в настройках Windows.</para>
///
/// <para>Читается реестр, а не <see cref="System.Net.Http.HttpClient.DefaultProxy"/>:
/// тот вычисляется один раз на процесс и после снятия показывал бы прежнее
/// значение.</para>
/// </summary>
public static class SystemProxy
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>Сколько ждём ответа порта. Прокси локальный — ему хватает и десятой доли.</summary>
    private static readonly TimeSpan AliveTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>Прокси включён и разобран — его состояние; выключен или не разобрать — null.</summary>
    public static SystemProxyState? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;

        using var key = Registry.CurrentUser.OpenSubKey(Key);
        if (key?.GetValue("ProxyEnable") is not int enabled || enabled == 0) return null;

        if (Parse(key.GetValue("ProxyServer") as string) is not var (host, port)) return null;

        return new SystemProxyState(host, port, Accepts(host, port));
    }

    /// <summary>
    /// Адрес из настройки. Бывает одной записью (<c>узел:порт</c>) и списком по
    /// протоколам (<c>http=узел:порт;https=узел:порт</c>) — берём первую запись.
    /// </summary>
    internal static (string Host, int Port)? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var first = raw.Split(';')[0].Trim();
        var eq = first.IndexOf('=');
        if (eq >= 0) first = first[(eq + 1)..];

        var colon = first.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(first[(colon + 1)..], out var port) || port is <= 0 or > 65535)
            return null;

        // IPv6 записывается в скобках — для подключения они не нужны.
        var host = first[..colon].Trim().TrimStart('[').TrimEnd(']');
        return host.Length == 0 ? null : (host, port);
    }

    /// <summary>
    /// Выключить системный прокси. Возвращает текст отказа или null.
    ///
    /// <para>Результат проверяется повторным чтением, а не кодом возврата: работающий
    /// клиент вроде Happ переписывает настройку обратно, и «готово» при вернувшемся
    /// прокси было бы худшим исходом — человек решит, что дело сделано.</para>
    /// </summary>
    public static string? Disable()
    {
        if (!OperatingSystem.IsWindows()) return "доступно только в Windows";

        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(Key, writable: true))
            {
                if (key is null) return "настройки прокси не найдены";
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            }

            // Без уведомления уже запущенные программы продолжат брать прокси из
            // своего кеша до перезапуска.
            InternetSetOption(IntPtr.Zero, OptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, OptionRefresh, IntPtr.Zero, 0);

            using var check = Registry.CurrentUser.OpenSubKey(Key);
            return check?.GetValue("ProxyEnable") is int value && value != 0
                ? "настройка вернулась сразу после снятия: её переписывает работающая программа, " +
                  "обычно другой VPN-клиент. Выключите в нём системный прокси и повторите."
                : null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return e.Message;
        }
    }

    /// <summary>То же, что <see cref="Read"/>, но вне потока интерфейса: проверка порта может ждать.</summary>
    public static Task<SystemProxyState?> ReadAsync() => Task.Run(Read);

    /// <summary>
    /// Принимает ли кто-нибудь подключение на адресе прокси.
    ///
    /// <para>Честный ответ — только для локального адреса, а это и есть случай чужих
    /// клиентов. Удалённый адрес при любом поднятом TUN «отвечает» всегда: рукопожатие
    /// принимает стек туннеля, а не сам прокси. Такой прокси покажется живым — и
    /// предупреждение про него будет мягким, а не ложной тревогой.</para>
    ///
    /// <para>Брошенную по таймауту попытку обязательно отменить и «прочитать»:
    /// закрытие сокета роняет её, и ничейное исключение всплыло бы в журнале
    /// необработанных ошибок на каждую проверку.</para>
    /// </summary>
    private static bool Accepts(string host, int port)
    {
        using var cancel = new CancellationTokenSource();
        using var client = new TcpClient();
        var attempt = client.ConnectAsync(host, port, cancel.Token).AsTask();

        try
        {
            return attempt.Wait(AliveTimeout) && client.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
        finally
        {
            cancel.Cancel();
            attempt.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private const int OptionRefresh = 37;
    private const int OptionSettingsChanged = 39;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);
}
