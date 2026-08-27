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

/// <summary>Код возврата границы. Значения — часть ABI, менять нельзя.</summary>
public enum McStatus
{
    Ok = 0,
    NullPointer = 1,
    InvalidHandle = 2,
    InvalidUtf8 = 3,
    InvalidArgument = 4,
    Duplicate = 5,
    NotFound = 6,
    StateInvalid = 7,
    Busy = 8,
    Poisoned = 9,
    Panic = 10,
    Internal = 11,
}

/// <summary>Отказ границы: код и объяснение из <c>mc_last_error</c>.</summary>
public sealed class MethodCoreException(McStatus status, string message)
    : Exception($"{message} (код {(int)status})")
{
    public McStatus Status { get; } = status;
}

/// <summary>
/// Граница общего ядра: разбор ссылок и сборка конфигурации движка.
///
/// <para><b>Почему через ядро, а не своим кодом.</b> Разбор share-ссылок и
/// схема конфигурации одинаковы на macOS, Android и здесь. Держать три
/// независимые реализации — значит трижды чинить одну ошибку и трижды
/// по-разному понимать один и тот же формат; расхождение обнаруживается не
/// сборкой, а у человека, у которого ссылка работает на телефоне и не работает
/// на настольной машине.</para>
///
/// <para><b>Правила владения.</b> Строки внутрь заимствуются: ядро их не
/// освобождает и после возврата на них не смотрит. Буфер наружу выдаётся во
/// владение вызывающего и освобождается только <c>mc_buffer_free</c> — куча
/// Rust и куча .NET разные, и освобождение чужой памяти своим освободителем
/// портит её не сразу, а потом.</para>
/// </summary>
public static partial class MethodCore
{
    private const string Library = "method_core_ffi";

    /// <summary>
    /// Версия границы, на которую рассчитан этот код. Сверяется при первом
    /// обращении: несовпадение ABI — это не «часть функций не работает», а
    /// чтение чужих структур по нашим смещениям.
    /// </summary>
    private const uint ExpectedAbi = 1;

    private static readonly object Gate = new();
    private static bool _checked;

    /// <summary>Строка внутрь: указатель на UTF-8 и длина в байтах.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct McStr
    {
        public IntPtr Ptr;
        public nuint Len;
    }

    /// <summary>Буфер наружу. Освобождается только <c>mc_buffer_free</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct McBuffer
    {
        public IntPtr Data;
        public nuint Len;
    }

    [LibraryImport(Library, EntryPoint = "mc_abi_version")]
    private static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "mc_last_error")]
    private static partial int LastError(out McStr text);

    [LibraryImport(Library, EntryPoint = "mc_buffer_free")]
    private static partial void BufferFree(ref McBuffer buffer);

    [LibraryImport(Library, EntryPoint = "mc_config_parse_link")]
    private static partial int ParseLink(McStr link, out McBuffer output);

    [LibraryImport(Library, EntryPoint = "mc_config_parse_many")]
    private static partial int ParseMany(McStr text, out McBuffer output);

    [LibraryImport(Library, EntryPoint = "mc_config_build_lanes")]
    private static partial int BuildLanes(McStr profiles, McStr policy, McStr options, out McBuffer output);

    /// <summary>Профиль одного узла из share-ссылки, JSON-ом.</summary>
    public static string ParseLink(string link) => Call(ParseLink, link);

    /// <summary>
    /// Тело подписки построчно: <c>{"profiles": [...], "errors": [...]}</c>.
    /// Кривая строка не роняет остальные — это принципиально: подписка на два
    /// десятка узлов, потерянная из-за одной записи, худший вид отказа.
    /// </summary>
    public static string ParseSubscription(string body) => Call(ParseMany, body);

    /// <summary>
    /// Конфигурация движка в схеме полос. Политика пустой строкой означает
    /// заводскую.
    /// </summary>
    public static string BuildLanes(string profilesJson, string policyJson, string optionsJson)
    {
        EnsureAbi();

        var profiles = Utf8.Rent(profilesJson);
        var policy = Utf8.Rent(policyJson);
        var options = Utf8.Rent(optionsJson);

        try
        {
            var code = BuildLanes(profiles.Str, policy.Str, options.Str, out var buffer);
            return Take(code, ref buffer);
        }
        finally
        {
            profiles.Dispose();
            policy.Dispose();
            options.Dispose();
        }
    }

    private delegate int Border(McStr input, out McBuffer output);

    private static string Call(Border border, string input)
    {
        EnsureAbi();

        var held = Utf8.Rent(input);

        try
        {
            var code = border(held.Str, out var buffer);
            return Take(code, ref buffer);
        }
        finally
        {
            held.Dispose();
        }
    }

    /// <summary>
    /// Забрать буфер и освободить его. Освобождаем в любом случае, включая
    /// отказ: буфер мог быть заполнен до ошибки.
    /// </summary>
    private static string Take(int code, ref McBuffer buffer)
    {
        try
        {
            if (code != (int)McStatus.Ok) throw Failure((McStatus)code);
            if (buffer.Data == IntPtr.Zero) throw Failure(McStatus.Internal);

            return Marshal.PtrToStringUTF8(buffer.Data, checked((int)buffer.Len));
        }
        finally
        {
            BufferFree(ref buffer);
        }
    }

    /// <summary>
    /// Объяснение отказа. Текст принадлежит ядру и живёт до следующего вызова
    /// из этого потока — копируем сразу.
    /// </summary>
    private static MethodCoreException Failure(McStatus status)
    {
        var message = "ядро отказало без объяснения";

        if (LastError(out var text) == (int)McStatus.Ok && text.Ptr != IntPtr.Zero && text.Len > 0)
            message = Marshal.PtrToStringUTF8(text.Ptr, checked((int)text.Len));

        return new MethodCoreException(status, message);
    }

    /// <summary>
    /// Сверка версии ABI. Один раз на процесс: библиотека не меняется на ходу,
    /// а проверять на каждом вызове — платить за то, что не меняется.
    /// </summary>
    private static void EnsureAbi()
    {
        if (_checked) return;

        lock (Gate)
        {
            if (_checked) return;

            var actual = AbiVersion();

            if (actual != ExpectedAbi)
                throw new MethodCoreException(
                    McStatus.StateInvalid,
                    $"версия границы ядра {actual}, а клиент собран под {ExpectedAbi}");

            _checked = true;
        }
    }

    /// <summary>
    /// Строка, закреплённая в памяти на время вызова. Ядро её только читает,
    /// но сборщик мусора об этом не знает — без закрепления он вправе сдвинуть
    /// массив ровно посреди чужого чтения.
    /// </summary>
    private readonly struct Utf8 : IDisposable
    {
        private readonly GCHandle _handle;

        public McStr Str { get; }

        private Utf8(GCHandle handle, McStr str)
        {
            _handle = handle;
            Str = str;
        }

        public static Utf8 Rent(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            return new Utf8(handle, new McStr
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
