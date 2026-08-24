package network.method.client;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * Главный экран: состояние, кнопка, путь и скорость.
 *
 * <p>Отличие от старого клиента не в оформлении, а в том, ЧТО показано. Там
 * был выбранный сервер и всё. Здесь под именем узла стоит строка пути — каким
 * СПОСОБОМ обхода трафик идёт прямо сейчас и сколько способов живо. Это и есть
 * суть клиента: узел выбирает человек один раз, а путь выбирается сам и
 * меняется без его участия — значит человек должен видеть, что происходит.
 */
final class HomeView extends LinearLayout {

    // Состояния объявлены здесь, потому что на них ссылаются готовые
    // компоненты вида. Значения совпадают со службой намеренно: два набора
    // констант для одного и того же — источник расхождений при первой же
    // правке.
    static final int DISCONNECTED = MethodVpnService.STATUS_DISCONNECTED;
    static final int CONNECTING = MethodVpnService.STATUS_CONNECTING;
    static final int CONNECTED = MethodVpnService.STATUS_CONNECTED;
    static final int ERROR = MethodVpnService.STATUS_ERROR;

    private final ConnectOrbView orb;
    private final StatusPill pill;
    private final TextView state;
    private final LinearLayout summary;
    private final View divider;
    private final SpeedCardView speed;
    private final TextView nodeName;
    private final TextView nodeAddress;
    private final TextView pathLine;
    private final TextView reasonLine;

    interface Callbacks {
        void onToggle();

        /** Открыть настройки. */
        void onSettings();
    }

    HomeView(Context ctx, Callbacks callbacks) {
        super(ctx);
        setOrientation(VERTICAL);
        setBackgroundColor(Ui.BG);
        setGravity(Gravity.CENTER_HORIZONTAL);
        int pad = Ui.dp(ctx, 20);
        setPadding(pad, Ui.dp(ctx, 20), pad, pad);
        Insets.pad(this);

        // Шапка: имя слева, шестерёнка справа. Значок вместо слова — настройки
        // открывают редко, и подпись занимала место, конкурируя с названием.
        LinearLayout header = new LinearLayout(ctx);
        header.setOrientation(HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);

        TextView brand = new TextView(ctx);
        brand.setText("Method");
        Ui.display(brand);
        brand.setTextSize(TypedValue.COMPLEX_UNIT_SP, 22);
        header.addView(brand, new LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f));

        FrameLayout gearBox = new FrameLayout(ctx);
        int gearSize = Ui.dp(ctx, 40);
        gearBox.setBackground(Deco.pill(ctx, Ui.LEVEL_1, Ui.LINE));
        Deco.tappable(gearBox, 100f);
        gearBox.setOnClickListener(v -> callbacks.onSettings());
        IconView gear = new IconView(ctx, IconView.GEAR, Ui.TEXT_2);
        int gi = Ui.dp(ctx, 19);
        FrameLayout.LayoutParams glp = new FrameLayout.LayoutParams(gi, gi);
        glp.gravity = Gravity.CENTER;
        gearBox.addView(gear, glp);
        header.addView(gearBox, new LayoutParams(gearSize, gearSize));
        addView(header, new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));

        addView(new View(ctx), new LayoutParams(0, 0, 1f));

        orb = new ConnectOrbView(ctx);
        orb.setOnClickListener(v -> callbacks.onToggle());
        LayoutParams orbLp = new LayoutParams(Ui.dp(ctx, 250), Ui.dp(ctx, 250));
        orbLp.gravity = Gravity.CENTER_HORIZONTAL;
        addView(orb, orbLp);

        // Состояние — КРУПНО и под кнопкой, а не мелкой плашкой в углу. Это
        // единственный вопрос, с которым человек открывает клиент, и ответ на
        // него должен читаться с расстояния вытянутой руки.
        state = new TextView(ctx);
        state.setTextSize(TypedValue.COMPLEX_UNIT_SP, 21);
        state.setTypeface(Ui.SEMIBOLD);
        state.setLetterSpacing(-0.01f);
        state.setGravity(Gravity.CENTER);
        addView(state, lp(Gravity.CENTER_HORIZONTAL, Ui.dp(ctx, 26)));

        pill = new StatusPill(ctx);
        pill.setVisibility(View.GONE);

        // Сводка сеанса: узел, путь и скорость ОДНИМ блоком. Раньше это были
        // три разрозненные строки и отдельная карточка — четыре объекта там,
        // где по смыслу один: чем и как мы сейчас идём.
        summary = new LinearLayout(ctx);
        summary.setOrientation(VERTICAL);
        summary.setBackground(Deco.raised(ctx, Ui.R_CARD, Ui.LEVEL_2, Ui.LEVEL_1, Ui.LINE));
        int sp = Ui.dp(ctx, 16);
        summary.setPadding(sp, Ui.dp(ctx, 14), sp, Ui.dp(ctx, 12));

        LinearLayout nodeRow = new LinearLayout(ctx);
        nodeRow.setOrientation(HORIZONTAL);
        nodeRow.setGravity(Gravity.CENTER_VERTICAL);

        IconView globe = new IconView(ctx, IconView.GLOBE, Ui.ACCENT);
        int gs = Ui.dp(ctx, 17);
        LayoutParams glp2 = new LayoutParams(gs, gs);
        glp2.rightMargin = Ui.dp(ctx, 11);
        nodeRow.addView(globe, glp2);

        LinearLayout nodeText = new LinearLayout(ctx);
        nodeText.setOrientation(VERTICAL);

        nodeName = new TextView(ctx);
        Ui.title(nodeName);
        nodeName.setSingleLine(true);
        nodeName.setEllipsize(android.text.TextUtils.TruncateAt.END);
        nodeText.addView(nodeName);

        nodeAddress = new TextView(ctx);
        Ui.label(nodeAddress);
        // Моноширинным: цифры одинаковой ширины не пляшут при смене узла.
        nodeAddress.setTypeface(Ui.MONO);
        nodeAddress.setSingleLine(true);
        nodeText.addView(nodeAddress);
        nodeRow.addView(nodeText, new LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f));
        summary.addView(nodeRow);

        // Строка пути. Ради неё всё и затевалось: узел — это ГДЕ, а путь — это
        // КАК. На одном узле разные пути отличаются втрое по задержке и в
        // тридцать раз по джиттеру.
        pathLine = new TextView(ctx);
        Ui.label(pathLine);
        pathLine.setPadding(0, Ui.dp(ctx, 8), 0, 0);
        summary.addView(pathLine);

        // Объяснение смены пути. Живёт недолго: оно описывает СОБЫТИЕ, а
        // висящее постоянно превращается в неправду.
        reasonLine = new TextView(ctx);
        Ui.label(reasonLine);
        reasonLine.setTextColor(Ui.WARN);
        reasonLine.setPadding(0, Ui.dp(ctx, 6), 0, 0);
        reasonLine.setVisibility(View.GONE);
        summary.addView(reasonLine);

        divider = new View(ctx);
        divider.setBackgroundColor(Ui.LINE);
        LayoutParams dlp2 = new LayoutParams(LayoutParams.MATCH_PARENT, 1);
        dlp2.topMargin = Ui.dp(ctx, 13);
        summary.addView(divider, dlp2);

        speed = new SpeedCardView(ctx);
        summary.addView(speed, new LayoutParams(LayoutParams.MATCH_PARENT,
                LayoutParams.WRAP_CONTENT));

        LayoutParams sumLp = new LayoutParams(LayoutParams.MATCH_PARENT,
                LayoutParams.WRAP_CONTENT);
        sumLp.topMargin = Ui.dp(ctx, 22);
        addView(summary, sumLp);

        addView(new View(ctx), new LayoutParams(0, 0, 1.25f));
        setState(DISCONNECTED);
    }

    private LayoutParams lp(int gravity, int topMargin) {
        LayoutParams p = new LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT);
        p.gravity = gravity;
        p.topMargin = topMargin;
        return p;
    }

    /** Состояние туннеля: {@link MethodVpnService} STATUS_*. */
    void setState(int status) {
        int orbState;
        switch (status) {
            case MethodVpnService.STATUS_CONNECTED: orbState = ConnectOrbView.CONNECTED; break;
            case MethodVpnService.STATUS_CONNECTING: orbState = ConnectOrbView.CONNECTING; break;
            default: orbState = ConnectOrbView.DISCONNECTED;
        }
        orb.setState(orbState);

        // Состояние словом и цветом. Цвет несёт смысл сам по себе: зелёное
        // читается боковым зрением, не вчитываясь, — а вчитываться в главный
        // ответ экрана человек не должен.
        switch (status) {
            case MethodVpnService.STATUS_CONNECTED:
                state.setText("Защищено");
                state.setTextColor(Ui.ACCENT);
                break;
            case MethodVpnService.STATUS_CONNECTING:
                state.setText("Подключаем…");
                state.setTextColor(Ui.WARN);
                break;
            case MethodVpnService.STATUS_ERROR:
                state.setText("Не удалось");
                state.setTextColor(Ui.DANGER);
                break;
            default:
                state.setText("Не защищено");
                state.setTextColor(Ui.TEXT_2);
        }

        // Сводку прячем целиком, когда рассказывать нечего. Пустая карточка на
        // отключённом экране — рамка вокруг ничего.
        summary.setVisibility(status == MethodVpnService.STATUS_CONNECTED
                ? View.VISIBLE : View.GONE);

        if (status != MethodVpnService.STATUS_CONNECTED) {
            speed.reset();
        }
    }

    /**
     * Узел, который сейчас несёт трафик.
     *
     * <p>Пустые строки ПРЯЧЕМ, а не оставляем пустыми: невидимый текст всё
     * равно занимает свой отступ, и экран без подключения расползался тремя
     * пустотами — между кнопкой, путём и скоростью. Разметка, в которой пустое
     * место остаётся от невидимого содержимого, выглядит небрежной ровно
     * настолько, насколько она небрежна.
     */
    void setNode(String name, String address) {
        boolean has = name != null && !name.trim().isEmpty();
        nodeName.setVisibility(has ? View.VISIBLE : View.GONE);
        nodeAddress.setVisibility(
                address != null && !address.trim().isEmpty() ? View.VISIBLE : View.GONE);
        nodeName.setText(name == null ? "" : name);
        nodeAddress.setText(address == null ? "" : address);
    }

    /**
     * @param path      человеческое имя оси: «gRPC в поддельном TLS»
     * @param aliveAxes сколько способов пройти живо
     * @param totalAxes сколько объявлено
     */
    void setPath(String path, int aliveAxes, int totalAxes) {
        if (path == null || path.isEmpty()) {
            pathLine.setText("");
            pathLine.setVisibility(View.GONE);
            return;
        }
        pathLine.setVisibility(View.VISIBLE);
        pathLine.setText("путь: " + path + " · " + aliveAxes + " "
                + Ui.plural(aliveAxes, "живой способ", "живых способа", "живых способов")
                + " из " + totalAxes);
    }

    /** Показать объяснение смены пути. Пустое — спрятать. */
    void setReason(String text) {
        if (text == null || text.trim().isEmpty()) {
            reasonLine.setVisibility(View.GONE);
            return;
        }
        reasonLine.setText(text);
        reasonLine.setVisibility(View.VISIBLE);
    }

    void pushSpeed(long downBps, long upBps) {
        speed.push(downBps, upBps);
    }
}
