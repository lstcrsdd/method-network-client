package network.method.client;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.RectF;
import android.view.Gravity;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.Locale;

/**
 * Карточка скорости: текущие вниз/вверх плюс полоски истории, как в макете.
 * История — фиксированное окно, поэтому карточка не меняет высоту и ничего
 * под ней не дёргается.
 */
final class SpeedCardView extends LinearLayout {
    private static final int SLOTS = 28;

    private final TextView downValue, upValue;
    private final TextView downUnit, upUnit;
    private final BarsView bars;

    SpeedCardView(Context ctx) {
        super(ctx);
        setOrientation(VERTICAL);
        int padH = Ui.dp(ctx, 14), padV = Ui.dp(ctx, 12);
        setPadding(padH, padV, padH, padV);
        // Фона нет: карточка живёт ВНУТРИ сводки сеанса. Своя рамка вокруг
        // рамки — вложенные прямоугольники, из-за которых экран выглядит
        // собранным из кусков.

        LinearLayout top = new LinearLayout(ctx);
        top.setOrientation(HORIZONTAL);
        top.setGravity(Gravity.CENTER_VERTICAL);

        int arrow = Ui.dp(ctx, 15);
        IconView down = new IconView(ctx, IconView.ARROW_DOWN, Ui.ACCENT);
        down.setWeight(2f);
        top.addView(down, new LayoutParams(arrow, arrow));
        downValue = value(ctx);
        LayoutParams dlp = new LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT);
        dlp.leftMargin = Ui.dp(ctx, 5);
        top.addView(downValue, dlp);
        downUnit = unit(ctx);
        top.addView(downUnit);

        IconView up = new IconView(ctx, IconView.ARROW_UP, Ui.TEXT_2);
        up.setWeight(2f);
        LayoutParams ulp = new LayoutParams(arrow, arrow);
        ulp.leftMargin = Ui.dp(ctx, 14);
        top.addView(up, ulp);
        upValue = value(ctx);
        LayoutParams uvlp = new LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT);
        uvlp.leftMargin = Ui.dp(ctx, 5);
        top.addView(upValue, uvlp);
        upUnit = unit(ctx);
        top.addView(upUnit);

        TextView caption = new TextView(ctx);
        caption.setText("СКОРОСТЬ");
        caption.setTextColor(Ui.TEXT_3);
        caption.setTextSize(8.5f);
        caption.setTypeface(Ui.MONO);
        caption.setLetterSpacing(0.13f);
        caption.setGravity(Gravity.END);
        caption.setSingleLine(true);
        top.addView(caption, new LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f));

        addView(top, new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));

        bars = new BarsView(ctx);
        LayoutParams blp = new LayoutParams(LayoutParams.MATCH_PARENT, Ui.dp(ctx, 34));
        blp.topMargin = Ui.dp(ctx, 10);
        addView(bars, blp);

        reset();
    }

    /** Новая точка истории. Значения в байтах в секунду. */
    void push(long downBps, long upBps) {
        downValue.setText(format(downBps));
        downUnit.setText(unitOf(downBps));
        upValue.setText(format(upBps));
        upUnit.setText(unitOf(upBps));
        bars.push(downBps);
    }

    void reset() {
        downValue.setText("0");
        downUnit.setText("КБ/с");
        upValue.setText("0");
        upUnit.setText("КБ/с");
        bars.clear();
    }

    /**
     * Число без единицы. Единицу отдаёт {@link #unitOf(long)}.
     *
     * <p>Единица меняется вместе с величиной, и это не украшение. Раньше всё
     * показывалось в мегабайтах с одним знаком, и обычный сёрфинг — десятки
     * килобайт в секунду — выглядел как «0.0»: человек видел ноль при
     * работающем туннеле и делал вывод, что клиент сломан. Так и вышло.
     */
    private static String format(long bps) {
        double mb = bps / 1_000_000d;
        if (mb >= 100) return String.format(Locale.ROOT, "%.0f", mb);
        if (mb >= 1) return String.format(Locale.ROOT, "%.1f", mb);
        // Ниже мегабайта считаем в килобайтах и без дробей: доли килобайта
        // не значат ничего, а лишний знак дёргается каждую секунду.
        return String.format(Locale.ROOT, "%d", Math.round(bps / 1000d));
    }

    private static String unitOf(long bps) {
        return bps >= 1_000_000 ? "МБ/с" : "КБ/с";
    }

    private TextView value(Context ctx) {
        TextView t = new TextView(ctx);
        t.setTextColor(Ui.TEXT);
        t.setTextSize(17);
        t.setTypeface(Ui.MONO);
        t.setSingleLine(true);
        return t;
    }

    private TextView unit(Context ctx) {
        TextView t = new TextView(ctx);
        t.setText("МБ/с");
        t.setTextColor(Ui.TEXT_3);
        t.setTextSize(9.5f);
        t.setTypeface(Ui.MEDIUM);
        t.setSingleLine(true);
        LayoutParams lp = new LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT);
        lp.leftMargin = Ui.dp(ctx, 4);
        lp.bottomMargin = Ui.dp(ctx, 1);
        lp.gravity = Gravity.BOTTOM;
        t.setLayoutParams(lp);
        return t;
    }

    /** Полоски истории: рисуются в отведённой высоте, за края не выходят. */
    private static final class BarsView extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF r = new RectF();
        private final long[] history = new long[SLOTS];
        private long peak = 1;

        BarsView(Context ctx) { super(ctx); }

        void push(long value) {
            System.arraycopy(history, 1, history, 0, SLOTS - 1);
            history[SLOTS - 1] = Math.max(0, value);
            peak = 1;
            for (long v : history) peak = Math.max(peak, v);
            invalidate();
        }

        void clear() {
            java.util.Arrays.fill(history, 0L);
            peak = 1;
            invalidate();
        }

        @Override
        protected void onDraw(Canvas c) {
            float w = getWidth(), h = getHeight();
            if (w <= 0 || h <= 0) return;
            float gap = Ui.dpf(getContext(), 2.5f);
            float bw = (w - gap * (SLOTS - 1)) / SLOTS;
            float radius = Math.min(bw / 2f, Ui.dpf(getContext(), 1.5f));
            float minH = Ui.dpf(getContext(), 2f);

            for (int i = 0; i < SLOTS; i++) {
                float ratio = peak <= 1 ? 0f : (float) history[i] / peak;
                float bh = Math.max(minH, ratio * h);
                float left = i * (bw + gap);
                r.set(left, h - bh, left + bw, h);
                boolean empty = history[i] <= 0;
                paint.setColor(empty ? Ui.alpha(0xFFFFFFFF, 0.07f)
                        : Ui.alpha(Ui.ACCENT, 0.35f + 0.55f * ratio));
                c.drawRoundRect(r, radius, radius, paint);
            }
        }
    }
}
