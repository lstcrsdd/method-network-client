package network.method.client;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.view.View;

/**
 * Мелкий векторный набор иконок. Специально не эмодзи: те приходят цветными,
 * по-разному отрисовываются на разных прошивках и ломают выравнивание строки.
 * Здесь всё рисуется в квадрате 24x24 и масштабируется под размер View.
 */
final class IconView extends View {
    static final int SHIELD = 0, SHIELD_LOCK = 1, GLOBE = 2, GEAR = 3,
            BOLT = 4, CHEVRON = 5, REFRESH = 6, ARROW_DOWN = 7, ARROW_UP = 8, CHECK = 9,
            TRASH = 10, SEARCH = 11;

    private final Paint stroke = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path path = new Path();
    private final RectF rect = new RectF();

    private int icon;
    private float weight = 2f;

    IconView(Context ctx, int icon, int color) {
        super(ctx);
        this.icon = icon;
        stroke.setStyle(Paint.Style.STROKE);
        stroke.setStrokeCap(Paint.Cap.ROUND);
        stroke.setStrokeJoin(Paint.Join.ROUND);
        setColor(color);
    }

    void setColor(int color) {
        stroke.setColor(color);
        fill.setColor(color);
        invalidate();
    }

    void setIcon(int next) {
        if (next == icon) return;
        icon = next;
        invalidate();
    }

    void setWeight(float dp) {
        weight = dp;
        invalidate();
    }

    /** Точки дуги радиуса r от a1 до a2 (градусы) — для сборки зубчатого контура. */
    private static void addArcPoints(Path p, float a1, float a2, float r, boolean moveFirst) {
        int steps = Math.max(2, Math.round(Math.abs(a2 - a1) / 6f));
        for (int i = 0; i <= steps; i++) {
            double a = Math.toRadians(a1 + (a2 - a1) * i / (float) steps);
            float x = 12f + (float) Math.cos(a) * r;
            float y = 12f + (float) Math.sin(a) * r;
            if (moveFirst && i == 0) p.moveTo(x, y); else p.lineTo(x, y);
        }
    }

    @Override
    protected void onDraw(Canvas c) {
        float size = Math.min(getWidth(), getHeight());
        if (size <= 0) return;
        float s = size / 24f;
        float dx = (getWidth() - size) / 2f, dy = (getHeight() - size) / 2f;

        c.save();
        c.translate(dx, dy);
        c.scale(s, s);
        stroke.setStrokeWidth(weight);
        path.reset();

        switch (icon) {
            case SHIELD:
            case SHIELD_LOCK:
                path.moveTo(12f, 3f);
                path.lineTo(20f, 6.2f);
                path.lineTo(20f, 12f);
                path.cubicTo(20f, 16.6f, 16.6f, 20f, 12f, 21.4f);
                path.cubicTo(7.4f, 20f, 4f, 16.6f, 4f, 12f);
                path.lineTo(4f, 6.2f);
                path.close();
                c.drawPath(path, stroke);
                if (icon == SHIELD_LOCK) {
                    rect.set(9.6f, 11.4f, 14.4f, 15.6f);
                    c.drawRoundRect(rect, 1.1f, 1.1f, fill);
                    path.reset();
                    path.moveTo(10.5f, 11.4f);
                    path.lineTo(10.5f, 10f);
                    path.cubicTo(10.5f, 8.2f, 13.5f, 8.2f, 13.5f, 10f);
                    path.lineTo(13.5f, 11.4f);
                    c.drawPath(path, stroke);
                }
                break;

            case GLOBE:
                c.drawCircle(12f, 12f, 8.6f, stroke);
                rect.set(7.2f, 3.4f, 16.8f, 20.6f);
                c.drawOval(rect, stroke);
                c.drawLine(3.4f, 12f, 20.6f, 12f, stroke);
                break;

            case GEAR: {
                // Настоящая шестерня: зубчатый контур с дыркой посередине.
                // Раньше рисовалась кольцом со спицами и читалась как солнце.
                final int teeth = 7;
                final float rOut = 9.4f, rIn = 6.9f;
                final float step = 360f / teeth;
                final float half = step * 0.30f;   // половина ширины зуба
                path.setFillType(Path.FillType.EVEN_ODD);
                for (int i = 0; i < teeth; i++) {
                    float base = i * step - 90f;
                    addArcPoints(path, base - half, base + half, rOut, i == 0);
                    addArcPoints(path, base + half, base + step - half, rIn, false);
                }
                path.close();
                path.addCircle(12f, 12f, 3.2f, Path.Direction.CCW);
                c.drawPath(path, fill);
                break;
            }

            case BOLT:
                path.moveTo(13.4f, 2.6f);
                path.lineTo(5.6f, 13.4f);
                path.lineTo(11f, 13.4f);
                path.lineTo(10.2f, 21.4f);
                path.lineTo(18.4f, 10.4f);
                path.lineTo(12.8f, 10.4f);
                path.close();
                c.drawPath(path, fill);
                break;

            case CHEVRON:
                path.moveTo(9.5f, 5f);
                path.lineTo(16f, 12f);
                path.lineTo(9.5f, 19f);
                c.drawPath(path, stroke);
                break;

            case TRASH: {
                // Корзина: крышка с ручкой и корпус с двумя рёбрами. Значок
                // необратимого действия обязан читаться мгновенно — здесь
                // стояли стрелки обновления, то есть ровно противоположное
                // тому, что кнопка делает.
                c.drawLine(3.4f, 6.2f, 20.6f, 6.2f, stroke);
                path.moveTo(9f, 6.2f);
                path.lineTo(9.6f, 3.6f);
                path.lineTo(14.4f, 3.6f);
                path.lineTo(15f, 6.2f);
                c.drawPath(path, stroke);
                path.reset();
                path.moveTo(5.6f, 6.2f);
                path.lineTo(6.8f, 20.4f);
                path.lineTo(17.2f, 20.4f);
                path.lineTo(18.4f, 6.2f);
                c.drawPath(path, stroke);
                c.drawLine(10.2f, 9.8f, 10.6f, 16.8f, stroke);
                c.drawLine(13.8f, 9.8f, 13.4f, 16.8f, stroke);
                break;
            }
            case SEARCH:
                rect.set(4.2f, 4.2f, 16.2f, 16.2f);
                c.drawArc(rect, 0f, 360f, false, stroke);
                c.drawLine(15.4f, 15.4f, 20.2f, 20.2f, stroke);
                break;
            case REFRESH:
                rect.set(4.4f, 4.4f, 19.6f, 19.6f);
                c.drawArc(rect, -35f, 285f, false, stroke);
                path.moveTo(17.6f, 2.6f);
                path.lineTo(19.9f, 7.4f);
                path.lineTo(14.8f, 7.6f);
                path.close();
                c.drawPath(path, fill);
                break;

            case ARROW_DOWN:
                c.drawLine(12f, 4f, 12f, 19f, stroke);
                path.moveTo(6.6f, 13.6f);
                path.lineTo(12f, 20.4f);
                path.lineTo(17.4f, 13.6f);
                c.drawPath(path, stroke);
                break;

            case ARROW_UP:
                c.drawLine(12f, 20f, 12f, 5f, stroke);
                path.moveTo(6.6f, 10.4f);
                path.lineTo(12f, 3.6f);
                path.lineTo(17.4f, 10.4f);
                c.drawPath(path, stroke);
                break;

            case CHECK:
                c.drawCircle(12f, 12f, 9f, stroke);
                path.moveTo(7.8f, 12.3f);
                path.lineTo(10.8f, 15.3f);
                path.lineTo(16.4f, 8.9f);
                c.drawPath(path, stroke);
                break;
        }
        c.restore();
    }
}
