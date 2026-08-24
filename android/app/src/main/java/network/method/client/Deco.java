package network.method.client;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.view.View;

/**
 * Фоны в стилистике макета: полупрозрачная поверхность + тонкая рамка.
 * Вынесено сюда, чтобы карточки во всех вкладках были идентичны и не расползались
 * по разным местам с чуть разными радиусами.
 */
final class Deco {
    private Deco() {}

    /** Обычная карточка: поверхность + рамка. */
    static GradientDrawable card(Context c, float radiusDp) {
        return card(c, radiusDp, Ui.SURFACE, Ui.LINE);
    }

    static GradientDrawable card(Context c, float radiusDp, int fill, int stroke) {
        GradientDrawable d = new GradientDrawable();
        d.setShape(GradientDrawable.RECTANGLE);
        d.setCornerRadius(Ui.dpf(c, radiusDp));
        d.setColor(fill);
        d.setStroke(Math.max(1, Ui.dp(c, 1)), stroke);
        return d;
    }

    /**
     * Карточка с лёгким перепадом сверху вниз.
     *
     * <p>Плоская заливка на большом прямоугольнике читается как наклейка.
     * Перепад в несколько процентов светлоты даёт поверхности объём, оставаясь
     * ниже порога, на котором градиент замечают как приём.
     */
    static GradientDrawable raised(Context c, float radiusDp, int top, int bottom, int stroke) {
        GradientDrawable d = new GradientDrawable(
                GradientDrawable.Orientation.TOP_BOTTOM, new int[]{top, bottom});
        d.setShape(GradientDrawable.RECTANGLE);
        d.setCornerRadius(Ui.dpf(c, radiusDp));
        d.setStroke(Math.max(1, Ui.dp(c, 1)), stroke);
        return d;
    }

    /** Круглая/пилюльная подложка. */
    static GradientDrawable pill(Context c, int fill, int stroke) {
        return card(c, 100f, fill, stroke);
    }

    /** Добавляет отклик на нажатие, не трогая основной фон. */
    static void tappable(View v, float radiusDp, int rippleColor) {
        GradientDrawable mask = new GradientDrawable();
        mask.setCornerRadius(Ui.dpf(v.getContext(), radiusDp));
        mask.setColor(Color.WHITE);
        v.setForeground(new RippleDrawable(ColorStateList.valueOf(rippleColor), null, mask));
        v.setClickable(true);
    }

    static void tappable(View v, float radiusDp) {
        tappable(v, radiusDp, Ui.alpha(Ui.ACCENT, 0.16f));
    }

    /** Фон-подложка для иконки в строке (квадрат со скруглением). */
    /**
     * Заливка с волосяной линией по верхнему краю.
     *
     * <p>Толщина — ровно один пиксель устройства, а не один dp: линия-разделитель
     * должна быть тоньше всего, что рисует остальной интерфейс. В dp на плотном
     * экране она превращается в полосу в три пикселя и читается как рамка.
     */
    static Drawable topHairline(Context c, int fill, int line) {
        android.graphics.drawable.ShapeDrawable top =
                new android.graphics.drawable.ShapeDrawable(new android.graphics.drawable.shapes.RectShape());
        top.getPaint().setColor(line);
        android.graphics.drawable.ShapeDrawable body =
                new android.graphics.drawable.ShapeDrawable(new android.graphics.drawable.shapes.RectShape());
        body.getPaint().setColor(fill);

        android.graphics.drawable.LayerDrawable stack =
                new android.graphics.drawable.LayerDrawable(new Drawable[]{top, body});
        stack.setLayerInset(1, 0, 1, 0, 0);
        return stack;
    }

    static Drawable iconTile(Context c) {
        return card(c, 12f, Ui.SURFACE_2, Color.TRANSPARENT);
    }
}
