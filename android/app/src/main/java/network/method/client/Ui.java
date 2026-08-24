package network.method.client;

import android.content.Context;
import android.graphics.Typeface;
import android.util.TypedValue;
import android.widget.TextView;

/**
 * Дизайн-токены Method Network. Значения взяты один в один из макета
 * (UI for VPN / MNScreen): почти чёрная база с зеленоватым уклоном,
 * мятный акцент, полупрозрачные поверхности вместо сплошных заливок.
 */
final class Ui {
    private Ui() {}

    // Фон: база и приподнятые поверхности
    static final int BG = 0xFF070A09;
    static final int BG_ELEVATED = 0xFF0D1211;

    // Акценты
    static final int ACCENT = 0xFF34E28A;         // основной зелёный
    static final int WARN = 0xFFF2B24B;           // подключение
    static final int DANGER = 0xFFFF6B5A;         // ошибка

    // Текст
    static final int TEXT = 0xFFEAF2EE;
    static final int TEXT_2 = 0x9EEAF2EE;         // ~62%
    static final int TEXT_3 = 0x5CEAF2EE;         // ~36%

    // Поверхности и линии (белый с малой альфой поверх фона)
    static final int SURFACE = 0x0BFFFFFF;        // ~4.5%
    static final int SURFACE_2 = 0x16FFFFFF;      // ~8.5%
    static final int LINE = 0x16FFFFFF;           // ~8.5%

    // Радиусы, выверенные по макету
    static final float R_CARD = 19f;
    static final float R_TILE = 16f;
    static final float R_ROW = 17f;
    static final float R_CHIP = 15f;

    static int dp(Context c, float v) {
        return Math.round(dpf(c, v));
    }

    static float dpf(Context c, float v) {
        return TypedValue.applyDimension(
                TypedValue.COMPLEX_UNIT_DIP, v, c.getResources().getDisplayMetrics());
    }

    /** Цвет с заданной альфой (0..1), RGB сохраняется. */
    static int alpha(int color, float a) {
        int aa = Math.max(0, Math.min(255, Math.round(a * 255)));
        return (color & 0x00FFFFFF) | (aa << 24);
    }

    /** Линейная интерполяция между двумя ARGB-цветами. */
    static int lerpColor(int from, int to, float t) {
        t = clamp01(t);
        int fa = (from >>> 24), fr = (from >> 16) & 0xFF, fg = (from >> 8) & 0xFF, fb = from & 0xFF;
        int ta = (to >>> 24), tr = (to >> 16) & 0xFF, tg = (to >> 8) & 0xFF, tb = to & 0xFF;
        int a = Math.round(fa + (ta - fa) * t);
        int r = Math.round(fr + (tr - fr) * t);
        int g = Math.round(fg + (tg - fg) * t);
        int b = Math.round(fb + (tb - fb) * t);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    static float lerp(float a, float b, float t) {
        return a + (b - a) * clamp01(t);
    }

    static float clamp01(float v) {
        return v < 0 ? 0 : (v > 1 ? 1 : v);
    }

    // Гарнитуры. Manrope из макета в системе нет — берём Roboto тех же весов,
    // моноширинный для цифр (время, IP, пинг), как в макете.
    static final Typeface REGULAR = Typeface.create("sans-serif", Typeface.NORMAL);
    static final Typeface MEDIUM = Typeface.create("sans-serif-medium", Typeface.NORMAL);
    static final Typeface SEMIBOLD = Typeface.create("sans-serif-medium", Typeface.BOLD);
    static final Typeface MONO = Typeface.create("monospace", Typeface.NORMAL);

    // Тональные поверхности. В тёмной теме высота выражается СВЕТЛОТОЙ: то, что
    // ближе к человеку, ловит больше света. Три уровня — потолок осмысленного:
    // дальше разница между соседними перестаёт читаться, и «уровни» становятся
    // враньём, которое видит только автор.
    /** Лист поверх фона: карточки, строки списка. */
    static final int LEVEL_1 = 0xFF101513;
    /** Приподнятое: выбранная строка, поле ввода в фокусе. */
    static final int LEVEL_2 = 0xFF171C1A;
    /** Оторванное от плоскости: диалог, всплывающая панель. */
    static final int LEVEL_3 = 0xFF1E2422;

    /**
     * Типографическая шкала.
     *
     * <p>Роли, а не размеры. Пока каждый экран назначал шрифт руками, набралось
     * восемь разных размеров заголовка и четыре начертания для одного и того же
     * по смыслу текста — разнобой, который поодиночке незаметен, а вместе
     * читается как небрежность.
     *
     * <p>Трекинг отрицательный у крупного и положительный у мелкого: на больших
     * кеглях межбуквенные просветы зрительно растут, на малых — схлопываются.
     * Это компенсация, а не украшение.
     */
    static void display(TextView t) {
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, 26);
        t.setTypeface(SEMIBOLD);
        t.setTextColor(TEXT);
        t.setLetterSpacing(-0.02f);
    }

    static void title(TextView t) {
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        t.setTypeface(MEDIUM);
        t.setTextColor(TEXT);
        t.setLetterSpacing(-0.01f);
    }

    static void body(TextView t) {
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
        t.setTypeface(REGULAR);
        t.setTextColor(TEXT_2);
        t.setLineSpacing(dpf(t.getContext(), 3f), 1f);
    }

    /** Пояснение под заголовком: то, что читают один раз и больше не возвращаются. */
    static void caption(TextView t) {
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12);
        t.setTypeface(REGULAR);
        t.setTextColor(TEXT_3);
        t.setLineSpacing(dpf(t.getContext(), 2.5f), 1f);
    }

    /** Подпись-ярлык: состояние, счётчик, единица измерения. */
    static void label(TextView t) {
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11.5f);
        t.setTypeface(MEDIUM);
        t.setTextColor(TEXT_3);
        t.setLetterSpacing(0.01f);
    }

    /** Заголовок раздела: разрядка вместо размера, чтобы не спорить с содержимым. */
    static void section(TextView t) {
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11);
        t.setTypeface(MEDIUM);
        t.setTextColor(TEXT_3);
        t.setLetterSpacing(0.12f);
        t.setAllCaps(true);
    }

    /**
     * Склонение существительного при числе.
     *
     * <p>«5 способа пройти» выдаёт небрежность мгновенно — сильнее, чем кривой
     * отступ: язык человек знает лучше, чем сетку. Правило русского счёта: 1 —
     * первая форма, 2–4 — вторая, всё остальное — третья, причём числа от 11
     * до 14 идут по третьей вопреки последней цифре.
     */
    static String plural(long n, String one, String few, String many) {
        long abs = Math.abs(n) % 100;
        if (abs >= 11 && abs <= 14) return many;
        switch ((int) (abs % 10)) {
            case 1: return one;
            case 2: case 3: case 4: return few;
            default: return many;
        }
    }

    /** Кнопка. */
    static void action(TextView t) {
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        t.setTypeface(MEDIUM);
        t.setLetterSpacing(0.01f);
    }
}
