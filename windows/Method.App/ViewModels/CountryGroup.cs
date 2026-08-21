// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Collections.ObjectModel;

namespace Method.App.ViewModels;

/// <summary>
/// Узлы одной страны.
///
/// <para>Провайдер присылает список в своём порядке — обычно по протоколам, и
/// страны в нём вперемешку. Человек же выбирает сначала место, а уже потом
/// способ туда попасть: искать три литовских узла по всему списку он не должен.</para>
/// </summary>
public sealed class CountryGroup(string? code, string? name)
{
    public string? Code { get; } = code;

    /// <summary>Название страны, а для неопознанных — честное «прочие».</summary>
    public string Name { get; } = name ?? "Прочие";

    public ObservableCollection<ServerItem> Servers { get; } = [];

    public string Header => $"{Name.ToUpperInvariant()} · {Servers.Count}";

    /// <summary>
    /// Порядок стран. Неопознанные уходят в конец: они не место, а остаток, и
    /// вклиниваться между настоящими странами им незачем.
    /// </summary>
    public string SortKey => Code is null ? "￿" : Name;
}
