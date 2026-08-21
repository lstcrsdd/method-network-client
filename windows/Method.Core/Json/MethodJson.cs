// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Method.Core.Json;

/// <summary>Настройки сериализации, общие для всего клиента.</summary>
public static class MethodJson
{
    /// <summary>
    /// Пустые поля не пишутся: на macOS отсутствующее значение отсутствует и в
    /// файле, а не лежит как <c>null</c>. Профиль должен читаться там без правок,
    /// поэтому форма записи здесь такая же.
    ///
    /// Экранирование ослаблено намеренно: в именах узлов живут кириллица и «·», и
    /// по умолчанию они превратились бы в \uXXXX. Файл читают люди. Значения при
    /// этом не попадают ни в HTML, ни в JavaScript — только в JSON на диске и в
    /// конфигурацию sing-box.
    ///
    /// Эмодзи вне основной плоскости (флаги стран — это она и есть) ослабленный
    /// кодировщик всё равно экранирует парой суррогатов. Это проверено, а не
    /// предположено: на macOS файл в этом месте выглядит иначе. Смысл записи от
    /// этого не меняется — любой разборщик читает обе формы одинаково, и профиль
    /// переживает запись и чтение без потерь.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>То же самое, но с отступами — для файлов, которые открывают глазами.</summary>
    public static readonly JsonSerializerOptions Readable = new(Options)
    {
        WriteIndented = true,
    };
}
