// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Globalization;

namespace Method.Core.Tunnel;

/// <summary>
/// Журнал привилегированной части.
///
/// <para>Пишут в него одновременно: обработчики соединений, обработчик завершения
/// движка, надзор и два потока чтения вывода движка. Без общей блокировки обрезка
/// (чтение файла целиком и перезапись) конкурировала бы с дозаписью, и записи
/// терялись бы именно в момент аварии — когда журнал нужнее всего.</para>
///
/// <para><b>Чего здесь быть не должно.</b> Конфигурация целиком: в ней пароли,
/// ключи Reality и секреты обфускации. Ошибку писать обязательно, сам конфиг —
/// никогда.</para>
/// </summary>
public sealed class HelperLog
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;

    public HelperLog(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path ?? AppPaths.HelperLogFile;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string Path => _path;

    /// <summary>
    /// Записать строку. Не бросает никогда: журнал — вспомогательная вещь, и
    /// уронить из-за него привилегированную часть значит оставить машину с
    /// поднятым туннелем без управления.
    /// </summary>
    public void Write(string message)
    {
        lock (_lock)
        {
            try
            {
                var now = _clock();
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                LogRotation.TrimIfNeeded(_path, now);
                File.AppendAllText(_path, $"{now:O} {message}\n");
            }
            catch (Exception e)
            {
                // Писать в файл некуда — говорим в поток ошибок. Молчать нельзя:
                // именно так и вышло в первый раз, когда права на каталог урезались
                // раньше, чем выяснялось, что процесс не тот, кому туда писать
                // разрешено. Причина отказа тогда просто исчезла, и наружу вышел
                // голый код возврата.
                TryWriteFallback($"{message} [журнал недоступен: {e.GetType().Name}]");
            }
        }
    }

    /// <summary>Строка вывода самого движка. Помечается, чтобы отличать от наших записей.</summary>
    public void WriteEngine(string line) => Write("движок| " + line);

    private static void TryWriteFallback(string message)
    {
        try { Console.Error.WriteLine("Method: " + message); }
        catch { /* и туда нельзя — больше вариантов нет */ }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"HelperLog({_path})");
}
