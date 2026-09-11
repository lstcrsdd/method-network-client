// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Method.Core.Storage;

/// <summary>Что было сохранено и когда — по стенным часам.</summary>
/// <param name="State">Выгрузка ядра как есть: разбирать её наше дело не наше.</param>
/// <param name="Elapsed">Сколько настоящего времени прошло с сохранения.</param>
public sealed record CoreState(byte[] State, TimeSpan Elapsed);

/// <summary>
/// История измерений между запусками клиента.
///
/// <para>Без неё каждый запуск даёт несколько минут, когда все маршруты
/// одинаково недостоверны: ядро набирает уверенность десятками проб и до тех
/// пор честно отвечает «выбирать не из чего». С ней первая же успешная проба
/// возвращает маршруту прежнюю уверенность.</para>
///
/// <para>Хранится зашифрованной — тем же способом, что и профили. Паролей
/// внутри нет, но есть список маршрутов и то, когда и как они работали: по
/// такому файлу видно, чем человек пользуется и в какие часы. Для клиента,
/// который ставят ровно затем, чтобы этого не было видно, оставлять такое
/// открытым текстом — своя же дыра.</para>
///
/// <para>Ни одна неудача чтения не считается поломкой: нет файла, чужая машина,
/// испорченное содержимое — ответ один, «истории нет». Клиент тогда наберёт её
/// заново, и это ровно то поведение, что было до переноса.</para>
/// </summary>
public sealed class CoreStateStore(string directory, ISecretBox box)
{
    private readonly string _path = Path.Combine(directory, "core-state.bin");

    public static CoreStateStore Default(ISecretBox box) => new(AppPaths.UserDataDir, box);

    /// <summary>Прочитать сохранённое. Пусто — значит набирать историю заново.</summary>
    public CoreState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            var plain = box.Unprotect(File.ReadAllBytes(_path));
            if (plain is null) return null;

            var saved = JsonSerializer.Deserialize<Saved>(Encoding.UTF8.GetString(plain));
            if (saved?.State is not { Length: > 0 }) return null;

            var elapsed = DateTimeOffset.UtcNow - saved.At;

            // Часы могли уйти назад — сверка времени, смена пояса, севшая
            // батарейка. Отрицательный промежуток ядру давать нельзя, а «только
            // что» здесь безопаснее: свежесть замеров оно и само пересчитает по
            // первым же пробам.
            return new CoreState(saved.State, elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero);
        }
        catch (Exception)
        {
            // Испорченный файл, чужая машина, нет доступа. Для вызывающего это
            // одно и то же: истории нет.
            return null;
        }
    }

    /// <summary>
    /// Сохранить выгрузку ядра. Возвращает, вышло ли: молчаливый отказ здесь
    /// означал бы клиент, который каждый запуск начинает с нуля и не говорит
    /// почему.
    /// </summary>
    public bool Save(byte[] state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            if (state.Length == 0)
            {
                // Сохранять нечего — но и держать прежнее нельзя: оно уже не про
                // нынешний каталог маршрутов.
                if (File.Exists(_path)) File.Delete(_path);
                return true;
            }

            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(new Saved { At = DateTimeOffset.UtcNow, State = state });
            var sealed_ = box.Protect(Encoding.UTF8.GetBytes(json));

            // Пишем через временный файл: обрыв питания посреди записи иначе
            // оставил бы обрезанный файл, который прочитается как «истории нет»
            // — то есть тихо потеряет всё накопленное.
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, sealed_);
            File.Move(temp, _path, overwrite: true);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record Saved
    {
        [JsonPropertyName("at")]
        public DateTimeOffset At { get; init; }

        [JsonPropertyName("state")]
        public byte[]? State { get; init; }
    }
}
