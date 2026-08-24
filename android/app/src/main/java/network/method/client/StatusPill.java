package network.method.client;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.view.Gravity;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * Пилюля состояния в шапке: точка + подпись. Ширина считается по тексту,
 * поэтому пилюля не растягивает шапку и не наезжает на название.
 */
final class StatusPill extends LinearLayout {
    private final Dot dot;
    private final TextView label;

    StatusPill(Context ctx) {
        super(ctx);
        setOrientation(HORIZONTAL);
        setGravity(Gravity.CENTER_VERTICAL);
        int padH = Ui.dp(ctx, 12), padV = Ui.dp(ctx, 7);
        setPadding(padH, padV, padH, padV);

        dot = new Dot(ctx);
        int size = Ui.dp(ctx, 7);
        LayoutParams dlp = new LayoutParams(size, size);
        dlp.rightMargin = Ui.dp(ctx, 7);
        addView(dot, dlp);

        label = new TextView(ctx);
        label.setTextSize(11.5f);
        label.setTypeface(Ui.SEMIBOLD);
        label.setSingleLine(true);
        addView(label);

        setState(HomeView.DISCONNECTED);
    }

    void setState(int state) {
        int color;
        String text;
        switch (state) {
            case HomeView.CONNECTED:  color = Ui.ACCENT; text = "Защищено"; break;
            case HomeView.CONNECTING: color = Ui.WARN;   text = "Подключение"; break;
            case HomeView.ERROR:      color = Ui.DANGER; text = "Ошибка"; break;
            default:                  color = Ui.TEXT_3; text = "Не защищено";
        }
        label.setTextColor(state == HomeView.DISCONNECTED ? Ui.TEXT_2 : color);
        label.setText(text);
        dot.setColor(color, state == HomeView.CONNECTING);
        setBackground(Deco.pill(getContext(),
                state == HomeView.DISCONNECTED ? Ui.SURFACE : Ui.alpha(color, 0.12f),
                state == HomeView.DISCONNECTED ? Ui.LINE : Ui.alpha(color, 0.28f)));
    }

    /** Точка: в состоянии «подключение» мягко пульсирует, в остальных статична. */
    private static final class Dot extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private ValueAnimator pulse;
        private float phase = 1f;

        Dot(Context ctx) { super(ctx); }

        void setColor(int color, boolean animate) {
            paint.setColor(color);
            if (animate) start(); else stop();
            invalidate();
        }

        private void start() {
            if (pulse != null && pulse.isRunning()) return;
            pulse = ValueAnimator.ofFloat(0.35f, 1f);
            pulse.setDuration(760);
            pulse.setRepeatCount(ValueAnimator.INFINITE);
            pulse.setRepeatMode(ValueAnimator.REVERSE);
            pulse.addUpdateListener(a -> { phase = (float) a.getAnimatedValue(); invalidate(); });
            pulse.start();
        }

        private void stop() {
            if (pulse != null) { pulse.cancel(); pulse = null; }
            phase = 1f;
        }

        @Override
        protected void onDraw(Canvas c) {
            float r = Math.min(getWidth(), getHeight()) / 2f;
            paint.setAlpha(Math.round(255 * phase));
            c.drawCircle(getWidth() / 2f, getHeight() / 2f, r, paint);
            paint.setAlpha(255);
        }

        @Override
        protected void onDetachedFromWindow() {
            super.onDetachedFromWindow();
            stop();
        }
    }
}
