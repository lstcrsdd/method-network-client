// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

namespace Method.App.Services;

/// <summary>
/// Одно окно клиента на пользователя.
///
/// <para><b>Зачем.</b> Два окна живут на одних и тех же файлах — узлы, подписки,
/// настройки, — но у каждого свой список в памяти. Проверено вживую: подписку
/// удалили и добавили заново в одном окне, а второе держало прежнюю картину и
/// при первом же сохранении записало бы её поверх. Вдобавок у каждого свой
/// сторож, и оба дёргали бы один туннель.</para>
///
/// <para>Второй запуск не молчит и не ругается: он будит первое окно и выходит.
/// Человек, щёлкнувший ярлык, хотел увидеть клиент — он его и увидит.</para>
/// </summary>
public static class SingleInstance
{
    // «Local\» — в пределах сеанса пользователя: у другого человека на той же
    // машине свои файлы и своё окно.
    private const string MutexName = @"Local\network.method.client.gui";
    private const string ShowEventName = @"Local\network.method.client.show";

    private static Mutex? _mutex;
    private static EventWaitHandle? _show;

    /// <summary>
    /// Занять место единственного окна. Нет — место занято: первое окно уже
    /// разбужено, запуск надо завершить.
    /// </summary>
    public static bool TryClaim()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (created) return true;

        _show.Set();
        return false;
    }

    /// <summary>Будить окно, когда запустят ещё раз. Ждём на отдельном потоке.</summary>
    public static void OnSecondLaunch(Action show)
    {
        if (_show is null) return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                _show.WaitOne();
                show();
            }
        })
        {
            IsBackground = true,
            Name = "Второй запуск",
        };

        thread.Start();
    }
}
