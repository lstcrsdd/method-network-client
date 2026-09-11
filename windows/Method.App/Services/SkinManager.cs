// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Method.Core.Storage;

namespace Method.App.Services;

/// <summary>
/// Переключение оформления.
///
/// <para>Двух версий клиента как двух наборов исходников нет и не будет: логика
/// одна, расходиться ей негде. Оформление — это набор значений и надстройка над
/// общими стилями, и подменяются они на ходу.</para>
///
/// <para>Ради этого все ссылки на цвета в разметке динамические. Статическая
/// ссылка вычисляется один раз при разборе, и подмена набора её бы не тронула:
/// половина окна осталась бы в прежних цветах.</para>
/// </summary>
public static class SkinManager
{
    private const string BaseTheme = "avares://Method/Styles/Theme.axaml";
    private const string PosterTheme = "avares://Method/Styles/Theme.Poster.axaml";
    private const string PosterControls = "avares://Method/Styles/Controls.Poster.axaml";

    private static StyleInclude? _posterStyles;

    public static AppSkin Current { get; private set; } = AppSkin.Base;

    /// <summary>
    /// Оформление сменилось. Нужно тем частям интерфейса, которые при смене
    /// не перекрашиваются, а пересобираются заново, — там, где у оформлений
    /// расходится само устройство экрана, а не отделка.
    /// </summary>
    public static event Action<AppSkin>? Changed;

    /// <summary>Применить оформление. Пусто — вышло; иначе причина отказа.</summary>
    public static string? Apply(AppSkin skin)
    {
        var application = Application.Current;
        if (application is null) return "приложение ещё не готово";

        try
        {
            SwapTheme(application, skin == AppSkin.Poster ? PosterTheme : BaseTheme);
            SwapControls(application, skin == AppSkin.Poster);

            Current = skin;
            Changed?.Invoke(skin);
            return null;
        }
        catch (Exception e)
        {
            // Оформление не должно ронять клиент. Но и молчать нельзя: человек
            // нажал и ждёт, что вид поменяется.
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>
    /// Набор значений всегда первый в списке: остальные словари могут ссылаться
    /// на него, и порядок здесь — часть договора, а не случайность.
    /// </summary>
    private static void SwapTheme(Application application, string uri)
    {
        var dictionaries = application.Resources.MergedDictionaries;
        var replacement = new ResourceInclude(new Uri(uri)) { Source = new Uri(uri) };

        if (dictionaries.Count > 0) dictionaries[0] = replacement;
        else dictionaries.Add(replacement);
    }

    private static void SwapControls(Application application, bool poster)
    {
        if (poster)
        {
            if (_posterStyles is not null) return;

            _posterStyles = new StyleInclude(new Uri(PosterControls)) { Source = new Uri(PosterControls) };
            application.Styles.Add(_posterStyles);
            return;
        }

        if (_posterStyles is null) return;

        application.Styles.Remove(_posterStyles);
        _posterStyles = null;
    }
}
