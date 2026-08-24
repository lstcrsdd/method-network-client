package network.method.client;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.RadialGradient;
import android.graphics.RectF;
import android.graphics.Shader;
import android.view.View;
import android.view.animation.AccelerateDecelerateInterpolator;
import android.view.animation.DecelerateInterpolator;
import android.view.animation.LinearInterpolator;

/**
 * Орб подключения: объёмный шар, а вокруг него по наклонной орбите летит спутник.
 *
 * Объём даётся связкой из четырёх слоёв — тень под шаром, радиальная заливка со
 * смещённым источником света, внутренняя тень по нижней кромке и блик сверху.
 * Глубина полёта — из знака sin(θ): дальняя половина орбиты рисуется ДО шара
 * (спутник уходит за него, тускнеет и уменьшается), ближняя — ПОСЛЕ. Никакого
 * 3D-движка, только порядок отрисовки и масштаб.
 *
 * Всё рисуется на аппаратном слое: BlurMaskFilter сознательно не используется,
 * иначе каждый кадр уходил бы в software-растеризацию.
 */
final class ConnectOrbView extends View {
    static final int DISCONNECTED = 0;
    static final int CONNECTING = 1;
    static final int CONNECTED = 2;

    private static final int ORBITS = 3;
    /** Наклон плоскости орбиты к экрану — от него зависит, насколько «трёхмерно» читается полёт. */
    private static final float ORBIT_TILT = -18f;

    private final Paint glow = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint disc = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint ring = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint orbit = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint dot = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF arc = new RectF();

    private int state = DISCONNECTED;
    private float level = 0f;    // 0 отключено … 1 подключено
    private float satAngle = 0f; // положение спутника на орбите, градусы
    private float spin = 0f;     // спиннер режима «подключение»
    private float breath = 0f;
    private float burst = -1f;

    private float discRadius, haloRadius;
    private RadialGradient haloShader, discShader, innerShadeShader, specShader, shadowShader;

    private ValueAnimator levelAnim, phaseAnim, spinAnim, breathAnim, burstAnim;

    ConnectOrbView(Context context) {
        super(context);
        ring.setStyle(Paint.Style.STROKE);
        orbit.setStyle(Paint.Style.STROKE);
        orbit.setStrokeCap(Paint.Cap.ROUND);
    }

    void setState(int next) {
        if (next == state) return;
        boolean becameConnected = next == CONNECTED && state != CONNECTED;
        state = next;

        animateLevel(next == CONNECTED ? 1f : (next == CONNECTING ? 0.45f : 0f));
        if (next != DISCONNECTED) startPhase();
        if (next == CONNECTING) startSpin(); else stopSpin();
        if (next == CONNECTED) startBreath(); else stopBreath();
        if (becameConnected) fireBurst();
    }

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        float cx = w / 2f, cy = h / 2f;
        float side = Math.min(w, h);
        discRadius = side * 0.30f;
        haloRadius = side * 0.5f;

        ring.setStrokeWidth(Ui.dpf(getContext(), 1.1f));
        orbit.setStrokeWidth(Ui.dpf(getContext(), 1.4f));

        haloShader = new RadialGradient(
                cx, cy, haloRadius,
                new int[]{Ui.alpha(Ui.ACCENT, 0.28f), Ui.alpha(Ui.ACCENT, 0.09f), 0x00000000},
                new float[]{0.3f, 0.65f, 1f}, Shader.TileMode.CLAMP);

        // Источник света смещён вверх-влево, низ уходит в тень — это и есть объём.
        discShader = new RadialGradient(
                cx - discRadius * 0.34f, cy - discRadius * 0.44f, discRadius * 1.55f,
                new int[]{0xFF243733, 0xFF16211E, 0xFF0A100E, 0xFF050807},
                new float[]{0f, 0.36f, 0.7f, 1f}, Shader.TileMode.CLAMP);

        innerShadeShader = new RadialGradient(
                cx, cy + discRadius * 0.34f, discRadius,
                new int[]{0x00000000, 0x00000000, 0x73000000},
                new float[]{0f, 0.6f, 1f}, Shader.TileMode.CLAMP);

        specShader = new RadialGradient(
                cx - discRadius * 0.30f, cy - discRadius * 0.54f, discRadius * 0.8f,
                new int[]{0x24FFFFFF, 0x0AFFFFFF, 0x00FFFFFF},
                new float[]{0f, 0.4f, 1f}, Shader.TileMode.CLAMP);

        // Мягкая тень под шаром — градиентом, а не блюром, чтобы не терять аппаратный слой.
        shadowShader = new RadialGradient(
                cx, cy + discRadius * 0.92f, discRadius * 0.95f,
                new int[]{0x73000000, 0x33000000, 0x00000000},
                new float[]{0f, 0.5f, 1f}, Shader.TileMode.CLAMP);

        if (state != DISCONNECTED) startPhase();
    }

    /** Кисть глифа. Скруглённые концы: рубленые смотрятся дёшево на крупном круге. */
    private final Paint glyph = new Paint(Paint.ANTI_ALIAS_FLAG) {{
        setStyle(Style.STROKE);
        setStrokeCap(Cap.ROUND);
    }};

    @Override
    protected void onDraw(Canvas c) {
        float cx = getWidth() / 2f, cy = getHeight() / 2f;
        if (discShader == null) return;

        // Гало.
        if (level > 0.001f) {
            float b = 1f + breath * 0.05f;
            glow.setShader(haloShader);
            glow.setAlpha(Math.round(Ui.clamp01(level) * 255));
            c.save();
            c.scale(b, b, cx, cy);
            c.drawCircle(cx, cy, haloRadius, glow);
            c.restore();
            glow.setShader(null);
            glow.setAlpha(255);
        }

        // Орбита и дальняя часть спутника — до шара, чтобы уходить за него.
        drawOrbitPath(c, cx, cy);
        drawSatellite(c, cx, cy, true);

        // Тень под шаром.
        disc.setShader(shadowShader);
        c.save();
        c.scale(1f, 0.34f, cx, cy + discRadius * 0.92f);
        c.drawCircle(cx, cy + discRadius * 0.92f, discRadius * 0.95f, disc);
        c.restore();

        // Шар: заливка → внутренняя тень → блик.
        disc.setShader(discShader);
        c.drawCircle(cx, cy, discRadius, disc);
        disc.setShader(innerShadeShader);
        c.drawCircle(cx, cy, discRadius, disc);
        disc.setShader(specShader);
        c.drawCircle(cx, cy, discRadius, disc);
        disc.setShader(null);

        // Кромка: сверху светлее, снизу гаснет.
        arc.set(cx - discRadius, cy - discRadius, cx + discRadius, cy + discRadius);
        ring.setColor(Ui.lerpColor(
                Ui.alpha(0xFFFFFFFF, 0.18f), Ui.alpha(Ui.ACCENT, 0.85f), level));
        c.drawArc(arc, 185f, 170f, false, ring);
        ring.setColor(Ui.lerpColor(
                Ui.alpha(0xFFFFFFFF, 0.05f), Ui.alpha(Ui.ACCENT, 0.30f), level));
        c.drawArc(arc, 355f, 190f, false, ring);

        // Глиф питания. Единственная зелёная деталь внутри шара — по нашему
        // же правилу оформления: акцент несут только глиф и дуга кромки, корпус
        // остаётся нейтральным. Без глифа шар читается как украшение, а не как
        // кнопка: человеку неоткуда узнать, что по нему надо нажать.
        drawPowerGlyph(c, cx, cy);

        // Ближняя часть спутника — поверх шара.
        drawSatellite(c, cx, cy, false);

        // Всплеск в момент подключения.
        if (burst >= 0f) {
            ring.setColor(Ui.alpha(Ui.ACCENT, (1f - burst) * 0.45f));
            c.drawCircle(cx, cy, discRadius * (1f + burst * 0.8f), ring);
        }

        // Спиннер режима «подключение».
        if (state == CONNECTING) {
            float r = discRadius * 1.2f;
            arc.set(cx - r, cy - r, cx + r, cy + r);
            orbit.setColor(Ui.WARN);
            orbit.setAlpha(255);
            c.drawArc(arc, spin, 74f, false, orbit);
        }
    }

    /**
     * Знак питания: разомкнутое кольцо и черта вверх.
     *
     * <p>Пропорции взяты общепринятые (IEC 60417-5009), а не выдуманные:
     * разрыв сверху около 70°, черта от центра до верхней кромки кольца.
     * Узнаваемость здесь важнее оригинальности — это единственный элемент
     * экрана, который обязан быть понятен без единого слова.
     *
     * <p>Цвет ведёт себя как кромка шара: гаснет до белого при отключении и
     * разгорается зелёным при подключении, — так состояние читается даже
     * боковым зрением, не читая подпись.
     */
    private void drawPowerGlyph(Canvas c, float cx, float cy) {
        float r = discRadius * 0.30f;
        float stroke = Math.max(Ui.dpf(getContext(), 2f), discRadius * 0.035f);

        glyph.setStrokeWidth(stroke);
        glyph.setColor(Ui.lerpColor(
                Ui.alpha(0xFFFFFFFF, 0.34f), Ui.alpha(Ui.ACCENT, 0.95f), level));

        // Кольцо с разрывом сверху: начинаем сразу за разрывом и идём по кругу.
        arc.set(cx - r, cy - r, cx + r, cy + r);
        c.drawArc(arc, -55f, 290f, false, glyph);

        // Черта. Не доводим до самого центра — иначе на малых размерах
        // сливается с кольцом в кляксу.
        c.drawLine(cx, cy - r * 1.16f, cx, cy - r * 0.16f, glyph);
    }

    /** Спокойные концентрические кольца + наклонная орбита, по которой летит спутник. */
    private void drawOrbitPath(Canvas c, float cx, float cy) {
        for (int i = 0; i < ORBITS; i++) {
            float r = discRadius * (1.17f + i * 0.19f);
            float fade = 1f - i * 0.26f;
            ring.setColor(Ui.lerpColor(
                    Ui.alpha(0xFFFFFFFF, 0.07f * fade),
                    Ui.alpha(Ui.ACCENT, 0.30f * fade), level));
            c.drawCircle(cx, cy, r, ring);
        }

        // Сама трасса спутника — плоский эллипс, наклонённый к экрану.
        orbit.setColor(Ui.lerpColor(
                Ui.alpha(0xFFFFFFFF, 0.06f), Ui.alpha(Ui.ACCENT, 0.26f), level));
        c.save();
        c.rotate(ORBIT_TILT, cx, cy);
        float rx = orbitRx(), ry = orbitRy();
        arc.set(cx - rx, cy - ry, cx + rx, cy + ry);
        c.drawOval(arc, orbit);
        c.restore();
    }

    private float orbitRx() { return discRadius * 1.34f; }
    private float orbitRy() { return discRadius * 0.42f; }

    /**
     * Спутник. Идёт по наклонному эллипсу: sin(θ) > 0 — ближняя половина (рисуется
     * поверх шара, крупнее и ярче), sin(θ) < 0 — дальняя (уходит за шар, тускнеет
     * и уменьшается). Хвост — несколько затухающих точек позади по трассе.
     */
    private void drawSatellite(Canvas c, float cx, float cy, boolean back) {
        if (level < 0.25f) return;

        final int tail = 14;
        for (int i = tail; i >= 0; i--) {
            float ang = satAngle - i * 4.2f;
            double a = Math.toRadians(ang);
            float depth = (float) Math.sin(a);          // +1 ближе к нам, -1 дальше
            boolean isFront = depth >= 0f;
            if (isFront == back) continue;              // рисуем только свою половину

            float[] p = orbitPoint(cx, cy, a);
            float t = 1f - i / (float) tail;            // 1 — голова, 0 — хвост
            float depthScale = 0.62f + 0.38f * (depth + 1f) / 2f;
            float alpha = level * t * t * (0.35f + 0.65f * (depth + 1f) / 2f);

            if (i == 0) {
                // Голова: ядро + двойное свечение.
                float rr = Ui.dpf(getContext(), 3.4f) * depthScale;
                dot.setColor(Ui.alpha(Ui.ACCENT, alpha * 0.16f));
                c.drawCircle(p[0], p[1], rr * 3.6f, dot);
                dot.setColor(Ui.alpha(Ui.ACCENT, alpha * 0.36f));
                c.drawCircle(p[0], p[1], rr * 2.1f, dot);
                dot.setColor(Ui.alpha(0xFFFFFFFF, Math.min(1f, alpha * 1.1f)));
                c.drawCircle(p[0], p[1], rr, dot);
            } else {
                float rr = Ui.dpf(getContext(), 2.1f) * depthScale * (0.35f + 0.65f * t);
                dot.setColor(Ui.alpha(Ui.ACCENT, alpha * 0.75f));
                c.drawCircle(p[0], p[1], rr, dot);
            }
        }
    }

    /** Точка орбиты по углу с учётом наклона плоскости. */
    private float[] orbitPoint(float cx, float cy, double angle) {
        float lx = (float) Math.cos(angle) * orbitRx();
        float ly = (float) Math.sin(angle) * orbitRy();
        double t = Math.toRadians(ORBIT_TILT);
        return new float[]{
                cx + (float) (lx * Math.cos(t) - ly * Math.sin(t)),
                cy + (float) (lx * Math.sin(t) + ly * Math.cos(t))
        };
    }

    // --- анимации ---

    /** Равномерный полёт спутника по орбите. Линейная интерполяция обязательна:
     *  любое ускорение читается как рывок. */
    private void startPhase() {
        if (phaseAnim != null && phaseAnim.isRunning()) return;
        phaseAnim = ValueAnimator.ofFloat(0f, 360f);
        phaseAnim.setDuration(6200);
        phaseAnim.setRepeatCount(ValueAnimator.INFINITE);
        phaseAnim.setInterpolator(new LinearInterpolator());
        phaseAnim.addUpdateListener(a -> { satAngle = (float) a.getAnimatedValue(); invalidate(); });
        phaseAnim.start();
    }

    private void stopPhase() {
        if (phaseAnim != null) { phaseAnim.cancel(); phaseAnim = null; }
    }

    private void animateLevel(float target) {
        if (levelAnim != null) levelAnim.cancel();
        levelAnim = ValueAnimator.ofFloat(level, target);
        levelAnim.setDuration(620);
        levelAnim.setInterpolator(new AccelerateDecelerateInterpolator());
        levelAnim.addUpdateListener(a -> { level = (float) a.getAnimatedValue(); invalidate(); });
        levelAnim.addListener(new android.animation.AnimatorListenerAdapter() {
            @Override public void onAnimationEnd(android.animation.Animator a) {
                if (level < 0.02f) stopPhase();   // отключено — кадры больше не нужны
            }
        });
        levelAnim.start();
    }

    private void startSpin() {
        if (spinAnim != null && spinAnim.isRunning()) return;
        spinAnim = ValueAnimator.ofFloat(0f, 360f);
        spinAnim.setDuration(1050);
        spinAnim.setRepeatCount(ValueAnimator.INFINITE);
        spinAnim.setInterpolator(new LinearInterpolator());
        spinAnim.addUpdateListener(a -> { spin = (float) a.getAnimatedValue(); invalidate(); });
        spinAnim.start();
    }

    private void stopSpin() {
        if (spinAnim != null) { spinAnim.cancel(); spinAnim = null; }
    }

    private void startBreath() {
        if (breathAnim != null && breathAnim.isRunning()) return;
        breathAnim = ValueAnimator.ofFloat(0f, 1f);
        breathAnim.setDuration(3400);
        breathAnim.setRepeatCount(ValueAnimator.INFINITE);
        breathAnim.setRepeatMode(ValueAnimator.REVERSE);
        breathAnim.setInterpolator(new AccelerateDecelerateInterpolator());
        breathAnim.addUpdateListener(a -> { breath = (float) a.getAnimatedValue(); invalidate(); });
        breathAnim.start();
    }

    private void stopBreath() {
        if (breathAnim != null) { breathAnim.cancel(); breathAnim = null; }
        breath = 0f;
    }

    private void fireBurst() {
        if (burstAnim != null) burstAnim.cancel();
        burstAnim = ValueAnimator.ofFloat(0f, 1f);
        burstAnim.setDuration(720);
        burstAnim.setInterpolator(new DecelerateInterpolator());
        burstAnim.addUpdateListener(a -> { burst = (float) a.getAnimatedValue(); invalidate(); });
        burstAnim.addListener(new android.animation.AnimatorListenerAdapter() {
            @Override public void onAnimationEnd(android.animation.Animator a) { burst = -1f; invalidate(); }
        });
        burstAnim.start();
    }

    /** Вращение стоит останавливать, когда экран не виден — иначе греем батарею. */
    @Override
    protected void onWindowVisibilityChanged(int visibility) {
        super.onWindowVisibilityChanged(visibility);
        if (visibility == VISIBLE && state != DISCONNECTED) startPhase(); else stopPhase();
    }

    @Override
    protected void onDetachedFromWindow() {
        super.onDetachedFromWindow();
        stopPhase();
        stopSpin();
        stopBreath();
        if (levelAnim != null) levelAnim.cancel();
        if (burstAnim != null) burstAnim.cancel();
    }
}
