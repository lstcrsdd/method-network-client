// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

namespace Method.Core.Models;

/// <summary>Почему ссылку не удалось разобрать.</summary>
public enum ShareLinkFailure
{
    EmptyInput,
    UnsupportedScheme,
    UnsupportedTransport,
    MalformedUrl,
    MissingField,
}

/// <summary>
/// Ссылку разобрать не удалось. Причина хранится отдельным полем, а не только в
/// тексте: текст показывают человеку, а решения принимаются по причине.
/// </summary>
public sealed class ShareLinkException : Exception
{
    public ShareLinkFailure Failure { get; }

    /// <summary>Поле или значение, из-за которого разбор не удался.</summary>
    public string? Detail { get; }

    private ShareLinkException(ShareLinkFailure failure, string message, string? detail = null)
        : base(message)
    {
        Failure = failure;
        Detail = detail;
    }

    public static ShareLinkException EmptyInput() =>
        new(ShareLinkFailure.EmptyInput, "Пустая ссылка");

    public static ShareLinkException UnsupportedScheme(string scheme) =>
        new(ShareLinkFailure.UnsupportedScheme, $"Неподдерживаемый протокол: {scheme}", scheme);

    public static ShareLinkException UnsupportedTransport(string transport) =>
        new(ShareLinkFailure.UnsupportedTransport,
            $"Транспорт «{transport}» не поддерживается. Доступны tcp, grpc и ws.", transport);

    public static ShareLinkException MalformedUrl() =>
        new(ShareLinkFailure.MalformedUrl, "Некорректный формат ссылки");

    public static ShareLinkException MissingField(string field) =>
        new(ShareLinkFailure.MissingField, $"Отсутствует обязательное поле: {field}", field);
}

/// <summary>Ошибка на конкретной строке при разборе нескольких ссылок разом.</summary>
/// <param name="Line">Номер строки в том виде, в каком его видит человек: с единицы и с учётом пустых.</param>
public sealed record ShareLinkLineError(int Line, ShareLinkException Error);
