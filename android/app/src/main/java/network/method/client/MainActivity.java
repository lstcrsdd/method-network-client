package network.method.client;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Typeface;
import android.net.VpnService;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Главный экран клиента. */
public final class MainActivity extends Activity {

    private static final String TAG = "MethodClient";
    private static final int REQUEST_VPN = 1001;
    /** Сколько держим объяснение смены пути. Оно описывает событие, а не состояние. */
    private static final long REASON_LIFETIME_MS = 30_000;

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());

    private HomeView home;
    private TextView hint;
    private Store store;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        store = new Store(this);

        // Самопроверки. JNI и Keystore ломаются ТИХО: библиотека не той
        // архитектуры, сброшенный ключ, странная прошивка — всё это выглядит
        // как «приложение просто закрылось» или «серверы куда-то делись».
        // Строка в журнале превращает загадку в диагноз.
        Log.i(TAG, MethodCore.selfTest());
        try {
            String probe = "проверка";
            String sealed = Secrets.seal(probe);
            Log.i(TAG, Secrets.isSealed(sealed) && probe.equals(Secrets.open(sealed))
                    ? "хранилище шифруется" : "ШИФРОВАНИЕ ВЕРНУЛО НЕ ТО");
        } catch (Throwable t) {
            Log.e(TAG, "шифрование недоступно: " + t);
        }

        setContentView(buildUi());
        tickUi();
    }

    private View buildUi() {
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Ui.BG);

        home = new HomeView(this, new HomeView.Callbacks() {
            @Override public void onToggle() { toggle(); }
            @Override public void onSettings() {
                startActivity(new Intent(MainActivity.this, SettingsActivity.class));
            }
        });
        root.addView(home, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        // Нижняя строка: серверы и подсказка. Без списка серверов клиент
        // бесполезен — он приезжает пустым, это не забывчивость, а замысел.
        LinearLayout bottom = new LinearLayout(this);
        bottom.setOrientation(LinearLayout.VERTICAL);
        bottom.setGravity(Gravity.CENTER_HORIZONTAL);
        int pad = Ui.dp(this, 20);
        bottom.setPadding(pad, 0, pad, pad);

        View servers = bottomButton("Серверы", IconView.GLOBE, v -> showServers());
        View scenarios = bottomButton("Сценарии", IconView.BOLT, v -> showScenarios());

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams half = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        LinearLayout.LayoutParams half2 = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        half2.leftMargin = Ui.dp(this, 10);
        row.addView(servers, half);
        row.addView(scenarios, half2);
        bottom.addView(row, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        hint = new TextView(this);
        Ui.label(hint);
        hint.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams hlp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        hlp.topMargin = Ui.dp(this, 8);
        bottom.addView(hint, hlp);

        FrameLayout.LayoutParams blp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT);
        blp.gravity = Gravity.BOTTOM;
        root.addView(bottom, blp);
        return root;
    }

    /**
     * Кнопка нижнего ряда: значок и подпись.
     *
     * <p>Обе одинаковы по весу — это равные по частоте действия, и выделять
     * одну означало бы соврать про то, как клиентом пользуются.
     *
     * <p>Значок здесь не украшение: две одинаковые надписи в ряду опознаются
     * чтением, а значок —формой, то есть быстрее и не глядя.
     */
    private View bottomButton(String text, int icon, View.OnClickListener action) {
        LinearLayout b = new LinearLayout(this);
        b.setOrientation(LinearLayout.HORIZONTAL);
        b.setGravity(Gravity.CENTER);
        int pad = Ui.dp(this, 16);
        b.setPadding(pad, Ui.dp(this, 13), pad, Ui.dp(this, 13));
        b.setBackground(Deco.pill(this, Ui.LEVEL_1, Ui.LINE));
        Deco.tappable(b, 100f);
        b.setOnClickListener(action);

        IconView glyph = new IconView(this, icon, Ui.TEXT_2);
        int size = Ui.dp(this, 17);
        LinearLayout.LayoutParams ilp = new LinearLayout.LayoutParams(size, size);
        ilp.rightMargin = Ui.dp(this, 9);
        b.addView(glyph, ilp);

        TextView label = new TextView(this);
        label.setText(text);
        Ui.action(label);
        label.setTextColor(Ui.TEXT);
        b.addView(label);
        return b;
    }

    /**
     * Экран сценариев.
     *
     * <p>Правила попадают в конфиг ядра при ПОДКЛЮЧЕНИИ: живьём их поменять
     * нечем — через локальный интерфейс у sing-box меняется только выбор внутри
     * группы. Значит после правки надо переподключиться, и сказать об этом
     * обязан клиент. Молча сохранить правило и не применить его хуже, чем
     * отказаться его принять: список говорит «сделано» при прежнем поведении.
     */
    private void showScenarios() {
        startActivity(new Intent(this, ScenariosActivity.class));
    }

    /** Экран серверов. Тоже страница, а не диалог — по той же причине. */
    private void showServers() {
        startActivity(new Intent(this, ServersActivity.class));
    }

    /** Ввод ссылок или подписки. */
    private void askForSources() {
        EditText field = new EditText(this);
        field.setHint("ссылки или адрес подписки");
        field.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        field.setMinLines(4);
        field.setGravity(Gravity.TOP);

        new AlertDialog.Builder(this)
                .setTitle("Свои серверы")
                .setMessage("Клиент приезжает пустым: вставьте ссылки или адрес подписки.")
                .setView(field)
                .setPositiveButton("Добавить", (d, w) -> {
                    String text = field.getText().toString().trim();
                    if (!text.isEmpty()) worker.execute(() -> addFrom(text));
                })
                .setNeutralButton("Забыть все", (d, w) -> {
                    store.clear();
                    toast("серверы забыты");
                })
                .setNegativeButton("Отмена", null)
                .show();
    }

    private void addFrom(String text) {
        try {
            String payload = text;
            if (text.startsWith("http://") || text.startsWith("https://")) {
                payload = Subscriptions.fetch(text);
                store.rememberSubscription(text);
            }
            Store.Result r = store.add(payload);
            toast(r.ok
                    ? "добавлено узлов: " + r.added
                        + (r.rejected > 0 ? ", негодных строк: " + r.rejected : "")
                    : "не вышло: " + r.message);
        } catch (Throwable t) {
            toast("ошибка: " + t.getMessage());
        }
    }

    private void toggle() {
        // Тактильный отклик на подключение. Кнопка запускает то, что меняет
        // сетевой путь всего устройства, — она обязана отзываться телу, а не
        // только глазу: человек часто жмёт её не глядя.
        home.performHapticFeedback(android.view.HapticFeedbackConstants.CONTEXT_CLICK);
        int s = MethodVpnService.status;
        if (s == MethodVpnService.STATUS_CONNECTED || s == MethodVpnService.STATUS_CONNECTING) {
            Intent stop = new Intent(this, MethodVpnService.class);
            stop.setAction(MethodVpnService.ACTION_STOP);
            startService(stop);
            return;
        }
        if (store.profileCount() == 0) {
            // Ведём на страницу серверов, а не в диалог: человеку без серверов
            // нужен не ввод строки, а объяснение, почему список пуст.
            showServers();
            return;
        }
        // Разрешение на туннель спрашивается один раз, но спросить обязано
        // приложение — тихо поднять VpnService нельзя.
        Intent prepare = VpnService.prepare(this);
        if (prepare != null) startActivityForResult(prepare, REQUEST_VPN);
        else startTunnel();
    }

    @Override
    protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == REQUEST_VPN) {
            if (result == RESULT_OK) startTunnel();
            else toast("разрешение на туннель не выдано");
        }
    }

    private void startTunnel() {
        Intent start = new Intent(this, MethodVpnService.class);
        start.setAction(MethodVpnService.ACTION_START);
        startService(start);
    }

    /**
     * Обновление экрана раз в секунду.
     *
     * <p>Опрос, а не подписка: состояние живёт в службе, которая переживает
     * экран, и подписка потребовала бы отписки при каждом повороте телефона.
     * Секунда — не та частота, ради которой стоит городить это.
     */
    private void tickUi() {
        int s = MethodVpnService.status;
        home.setState(s);

        int count = store.profileCount();
        if (!MethodCore.isAvailable()) {
            hint.setText("общий слой не загрузился — клиент не сможет собрать конфиг");
            hint.setTextColor(Ui.DANGER);
            hint.setOnClickListener(null);
        } else if (count == 0) {
            hint.setText("серверов нет: вставьте ссылку или подписку");
            hint.setTextColor(Ui.TEXT_3);
            hint.setOnClickListener(null);
        } else if (batteryLimited() && s == MethodVpnService.STATUS_CONNECTED) {
            // Показываем ТОЛЬКО при поднятом туннеле. Раньше висело всегда, а
            // предупреждение, которое не исчезает, перестаёт быть
            // предупреждением: к нему привыкают за день и перестают видеть.
            // Пока туннеля нет — и терять нечего, говорить не о чем.
            //
            // Одной строкой: в две строки внизу экрана оно перетягивает
            // внимание с того, ради чего человек сюда пришёл.
            hint.setText("туннель может выключиться в фоне — разрешить");
            hint.setTextColor(Ui.WARN);
            hint.setOnClickListener(v -> askBatteryExemption());
        } else {
            hint.setText(count + " " + Ui.plural(count, "узел", "узла", "узлов"));
            hint.setTextColor(Ui.TEXT_3);
            hint.setOnClickListener(null);
        }

        if (s == MethodVpnService.STATUS_CONNECTED) {
            home.pushSpeed(MethodVpnService.downBps, MethodVpnService.upBps);
            // Узел приходит одной строкой «имя · адрес:порт» — режем на две,
            // чтобы имя читалось крупно, а адрес не мешал.
            String node = MethodVpnService.currentNode;
            if (node == null || node.isEmpty()) {
                home.setNode("подбираем узел…", null);
            } else {
                int cut = node.lastIndexOf(" · ");
                home.setNode(cut > 0 ? node.substring(0, cut) : node,
                        cut > 0 ? node.substring(cut + 3) : null);
            }
            home.setPath(MethodVpnService.currentPath,
                    MethodVpnService.aliveAxes, MethodVpnService.totalAxes);
            long age = System.currentTimeMillis() - MethodVpnService.lastReasonAt;
            home.setReason(age < REASON_LIFETIME_MS ? MethodVpnService.lastReason : null);
        } else {
            home.setNode(null, null);
            home.setPath(null, 0, 0);
            home.setReason(null);
        }

        ui.postDelayed(this::tickUi, 1000);
    }

    /**
     * Ограничивает ли система работу приложения в фоне.
     *
     * <p>Проверено на живом Xiaomi 2026-08-24: система убила процесс вместе с
     * пятью другими приложениями, пометив это очисткой памяти, — хотя служба
     * держалась на переднем плане с постоянным уведомлением, как положено.
     * Туннель при этом просто исчез: интернет продолжил работать, но уже
     * открытым. Разрешение было объявлено в манифесте с самого начала и
     * никогда не запрашивалось.
     */
    private boolean batteryLimited() {
        try {
            android.os.PowerManager pm = getSystemService(android.os.PowerManager.class);
            return pm != null && !pm.isIgnoringBatteryOptimizations(getPackageName());
        } catch (Throwable t) {
            return false;
        }
    }

    /**
     * Системный запрос на работу без ограничений.
     *
     * <p>Тихо снять ограничение нельзя и не нужно: решение принимает человек,
     * система показывает ему свой диалог. Мы объясняем, зачем, и открываем его.
     *
     * <p>Оболочки Xiaomi, Huawei и подобные держат СВОЙ список автозапуска,
     * которого системный диалог не касается. Сказать об этом прямо честнее,
     * чем оставить человека с выключающимся туннелем и снятой галочкой.
     */
    private void askBatteryExemption() {
        new AlertDialog.Builder(this)
                .setTitle("Работа в фоне")
                .setMessage("Система экономит батарею и может выключить туннель, "
                        + "когда приложение свёрнуто. Тогда интернет продолжит работать — "
                        + "но уже открытым, с вашим настоящим адресом.\n\n"
                        + "На оболочках Xiaomi этого мало: нужно ещё разрешить автозапуск "
                        + "в настройках приложения — системный диалог до него не достаёт.")
                .setPositiveButton("Разрешить", (d, w) -> {
                    try {
                        Intent i = new Intent(
                                android.provider.Settings
                                        .ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                                android.net.Uri.parse("package:" + getPackageName()));
                        startActivity(i);
                    } catch (Throwable t) {
                        toast("не вышло открыть настройки: " + t.getMessage());
                    }
                })
                .setNegativeButton("Позже", null)
                .show();
    }

    private void toast(String text) {
        Log.i(TAG, text);
        runOnUiThread(() -> android.widget.Toast.makeText(this, text,
                android.widget.Toast.LENGTH_LONG).show());
    }
}
