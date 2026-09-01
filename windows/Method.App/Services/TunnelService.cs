// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Runtime.Versioning;
using System.ServiceProcess;
using Method.Core.Tunnel;

namespace Method.App.Services;

/// <summary>
/// Служба Windows вокруг привилегированной части.
///
/// <para>Класс намеренно тонкий: он умеет только принять от диспетчера «пуск» и
/// «стоп» и перевести их в отмену для <see cref="TunnelHost"/>. Вся работа —
/// там, и она про службу ничего не знает. Так привилегированную часть можно
/// гонять в консоли и отлаживать, не поднимая службу вовсе.</para>
///
/// <para>Почему служба, а не запланированная задача, с которой начинали: задача
/// не имеет состояния, которое можно спросить, — «запущена» у неё означает
/// «действие выполняется», и отличить упавший процесс от невыполнявшейся задачи
/// нечем. У службы состояние есть, и его сообщает система, а не наши догадки.
/// Задача не умеет останавливаться корректно: снятие — это убийство процесса, а
/// убитый держатель туннеля оставляет за собой блокировку исходящего трафика.
/// И задача не запускается при выключенном планировщике, которого на урезанных
/// сборках Windows регулярно нет.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TunnelService : ServiceBase
{
    private readonly CancellationTokenSource _stopping = new();
    private Task<int>? _host;

    /// <summary>
    /// Сколько ждём, пока привилегированная часть свернётся. Диспетчер служб даёт
    /// на остановку тридцать секунд, и часть этого времени уходит на гашение
    /// движка и уборку адаптера. Ждать дольше запрошенной добавки нельзя: система
    /// сочтёт службу зависшей и убьёт её ровно так же, как убивала задача.
    /// </summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(20);

    private static readonly HelperLog Log = new();

    private TunnelService()
    {
        ServiceName = ServiceControl.ServiceName;
        CanStop = true;

        // Выключение машины — это тоже остановка, и без неё мы бы уходили вместе
        // с системой, не сняв блокировку. Она пережила бы перезагрузку и
        // встретила человека машиной без сети.
        CanShutdown = true;

        CanPauseAndContinue = false;

        // Пуск и остановку пишет сама система, в системный журнал. Это ровно то
        // место, куда пойдёт смотреть человек, у которого «клиент не работает», —
        // и туда мы своим журналом в ProgramData не дотянемся.
        AutoLog = true;
    }

    /// <summary>Отдать процесс диспетчеру служб. Возврат — когда служба остановлена.</summary>
    public static void RunUnderScm() => Run(new TunnelService());

    protected override void OnStart(string[] args)
    {
        // Диспетчеру нельзя отвечать долго: обработчик обязан вернуться, а работа
        // — идти своим ходом. Запуск привилегированной части включает уборку
        // адаптера и извлечение движка, это секунды, и держать на них диспетчер
        // незачем.
        _host = Task.Run(() => TunnelHost.RunAsync(_stopping.Token));

        // Отказ привилегированной части — это остановленная служба с ненулевым
        // кодом, а не служба в состоянии «работает». Иначе система рапортует
        // «выполняется», интерфейс говорит «не отвечает», и человеку остаётся
        // гадать, кто из двоих врёт.
        _ = _host.ContinueWith(
            finished =>
            {
                var code = finished.IsFaulted ? 1 : finished.Result;
                if (code == 0) return; // штатный выход по отмене

                Log.Write($"привилегированная часть завершилась с кодом {code}, останавливаю службу");
                ExitCode = code;

                try { Stop(); }
                catch (Exception e) { Log.Write($"остановить службу не удалось: {e.Message}"); }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    protected override void OnStop() => Wind(down: "остановка службы");

    protected override void OnShutdown() => Wind(down: "выключение машины");

    /// <summary>
    /// Свернуться. Отмена доходит до цикла канала, тот выходит, и
    /// <see cref="TunnelHost"/> сам гасит движок и снимает блокировку.
    /// </summary>
    private void Wind(string down)
    {
        Log.Write(down);

        // Просим добавку до того, как начинаем ждать: запрошенная после
        // истечения срока добавка уже никого не касается.
        try { RequestAdditionalTime((int)StopWait.TotalMilliseconds + 5_000); }
        catch (Exception e) { Log.Write($"добавку времени запросить не вышло: {e.Message}"); }

        _stopping.Cancel();

        try
        {
            if (_host is not null && !_host.Wait(StopWait))
                Log.Write("ВНИМАНИЕ: привилегированная часть не свернулась за отведённое время");
        }
        catch (Exception e)
        {
            Log.Write($"ожидание завершения: {e.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _stopping.Dispose();
        base.Dispose(disposing);
    }
}
