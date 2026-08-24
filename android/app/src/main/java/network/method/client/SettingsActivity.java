package network.method.client;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Bundle;
import android.os.PowerManager;
import android.provider.Settings;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/**
 * Настройки.
 *
 * <p>Здесь только то, что человек действительно может решить, и ничего для
 * красоты. Настройка, которую невозможно осмысленно выбрать, — это вопрос,
 * переложенный с разработчика на пользователя.
 *
 * <p>Двух вещей здесь нет намеренно. Выбора сервера — его делает клиент сам,
 * и делает лучше человека: на одном узле разные пути отличаются втрое по
 * задержке. Выбора протокола — по той же причине, это и есть работа движка.
 */
public final class SettingsActivity extends Activity {

    private static final String FILE = "method-settings";
    static final String KEY_AUTOSTART = "autostart";

    private LinearLayout column;
    private SharedPreferences prefs;

    /** Поднимать ли туннель при загрузке телефона. Читает {@link BootReceiver}. */
    static boolean autostart(Context ctx) {
        return ctx.getSharedPreferences(FILE, Context.MODE_PRIVATE)
                .getBoolean(KEY_AUTOSTART, false);
    }

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        prefs = getSharedPreferences(FILE, Context.MODE_PRIVATE);

        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(Ui.BG);
        column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        int pad = Ui.dp(this, 20);
        column.setPadding(pad, Ui.dp(this, 24), pad, pad);
        Insets.pad(column);
        scroll.addView(column);
        setContentView(scroll);
    }

    @Override
    protected void onResume() {
        super.onResume();
        // Пересобираем каждый раз: человек уходит в системные настройки и
        // возвращается, и состояние «разрешено / не разрешено» должно быть
        // тем, что есть сейчас, а не тем, что было при открытии экрана.
        rebuild();
    }

    private void rebuild() {
        column.removeAllViews();

        TextView title = new TextView(this);
        title.setText("Настройки");
        Ui.display(title);
        column.addView(title);

        column.addView(section("Защита"));

        // Единственный настоящий kill switch, доступный на Android. Сами мы
        // его включить не можем — и не должны: это системная настройка,
        // которая переживает выключение приложения, потому и работает.
        column.addView(row(
                "Всегда включённый VPN",
                "Без него при выключении клиента трафик пойдёт открытым. Включается "
                        + "в настройках Android: Сеть → VPN → Method → «Постоянный VPN» "
                        + "и «Блокировать соединения без VPN».",
                "Открыть настройки VPN",
                v -> openVpnSettings(), IconView.SHIELD_LOCK, Ui.ACCENT));

        boolean limited = batteryLimited();
        column.addView(row(
                "Работа в фоне",
                limited
                        ? "Сейчас система ограничивает приложение и может выключить туннель, "
                            + "когда экран погашен. Тогда интернет продолжит работать — "
                            + "но уже открытым."
                        : "Разрешена. Система не станет выключать туннель ради экономии батареи.",
                limited ? "Разрешить" : null,
                v -> askBattery(), IconView.BOLT, limited ? Ui.WARN : Ui.TEXT_2));

        column.addView(row(
                "Приложения",
                new AppRules(this).summary()
                        + " Здесь решается, попадёт ли трафик в туннель вообще — "
                        + "в отличие от сценариев, которые выбирают путь уже внутри него.",
                "Настроить",
                v -> startActivity(new Intent(this, AppsActivity.class)),
                IconView.GLOBE, Ui.TEXT_2));

        column.addView(section("Запуск"));
        column.addView(toggle(
                "Подключаться при включении телефона",
                "Туннель поднимется сам после перезагрузки. Оболочки Xiaomi и Huawei "
                        + "для этого требуют ещё и разрешить автозапуск в настройках приложения.",
                KEY_AUTOSTART));

        column.addView(section("Данные"));
        column.addView(row(
                "Забыть всё",
                "Серверы, подписки, сценарии и накопленные замеры. Отменить нельзя.",
                "Забыть",
                v -> confirmWipe(), IconView.TRASH, Ui.DANGER));

        column.addView(section("О клиенте"));
        column.addView(info());
    }

    private View section(String text) {
        TextView t = new TextView(this);
        t.setText(text);
        Ui.section(t);
        t.setPadding(Ui.dp(this, 4), Ui.dp(this, 24), 0, Ui.dp(this, 10));
        return t;
    }

    private View row(String title, String explain, String action, View.OnClickListener onAction) {
        return row(title, explain, action, onAction, -1, Ui.TEXT_2);
    }

    /**
     * Строка настройки: значок, заголовок, объяснение и — если есть что делать —
     * кнопка.
     *
     * <p>Значок слева не украшение: в списке из шести карточек с одинаковой
     * вёрсткой он единственное, за что глаз цепляется при беглом просмотре.
     * Цвет значка несёт состояние: тревожный там, где от человека чего-то ждут.
     */
    private View row(String title, String explain, String action,
                     View.OnClickListener onAction, int icon, int iconColor) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackground(Deco.raised(this, Ui.R_CARD, Ui.LEVEL_2, Ui.LEVEL_1, Ui.LINE));
        int pad = Ui.dp(this, 16);
        card.setPadding(pad, pad, pad, pad);

        LinearLayout headRow = new LinearLayout(this);
        headRow.setOrientation(LinearLayout.HORIZONTAL);
        headRow.setGravity(Gravity.CENTER_VERTICAL);
        if (icon >= 0) {
            IconView glyph = new IconView(this, icon, iconColor);
            int size = Ui.dp(this, 18);
            LinearLayout.LayoutParams ilp = new LinearLayout.LayoutParams(size, size);
            ilp.rightMargin = Ui.dp(this, 11);
            headRow.addView(glyph, ilp);
        }

        TextView head = new TextView(this);
        head.setText(title);
        Ui.title(head);
        headRow.addView(head);
        card.addView(headRow);

        TextView text = new TextView(this);
        text.setText(explain);
        Ui.caption(text);
        text.setPadding(0, Ui.dp(this, 6), 0, 0);
        card.addView(text);

        if (action != null) {
            TextView button = new TextView(this);
            button.setText(action);
            Ui.action(button);
            button.setTextColor(Ui.ACCENT);
            button.setGravity(Gravity.CENTER);
            button.setPadding(0, Ui.dp(this, 12), 0, Ui.dp(this, 12));
            button.setBackground(Deco.pill(this, Ui.LEVEL_2, Ui.LINE));
            // Отклик на нажатие. Кнопка, которая никак не отзывается, читается
            // как неживая: человек жмёт второй раз, не поняв, сработало ли.
            Deco.tappable(button, 100f);
            button.setOnClickListener(onAction);
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
            lp.topMargin = Ui.dp(this, 12);
            card.addView(button, lp);
        }

        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Ui.dp(this, 10);
        card.setLayoutParams(lp);
        return card;
    }

    /** Переключатель: сам заголовок и есть кнопка, состояние — справа словом. */
    private View toggle(String title, String explain, String key) {
        boolean on = prefs.getBoolean(key, false);

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackground(Deco.raised(this, Ui.R_CARD, Ui.LEVEL_2, Ui.LEVEL_1, Ui.LINE));
        int pad = Ui.dp(this, 16);
        card.setPadding(pad, pad, pad, pad);

        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);

        TextView name = new TextView(this);
        name.setText(title);
        Ui.title(name);
        head.addView(name, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        TextView state = new TextView(this);
        state.setText(on ? "включено" : "выключено");
        Ui.label(state);
        state.setTextColor(on ? Ui.ACCENT : Ui.TEXT_3);
        head.addView(state);
        card.addView(head);

        TextView text = new TextView(this);
        text.setText(explain);
        Ui.caption(text);
        text.setPadding(0, Ui.dp(this, 6), 0, 0);
        card.addView(text);

        Deco.tappable(card, Ui.R_CARD);
        card.setOnClickListener(v -> {
            prefs.edit().putBoolean(key, !on).apply();
            rebuild();
        });

        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Ui.dp(this, 10);
        card.setLayoutParams(lp);
        return card;
    }

    /**
     * Что важно знать при разборе поломки.
     *
     * <p>Самопроверки моста и хранилища идут при каждом запуске и падают тихо:
     * библиотека не той архитектуры или сброшенный ключ выглядят как «клиент
     * просто закрылся». Строка здесь превращает загадку в диагноз.
     */
    private View info() {
        String version = "неизвестно";
        try {
            version = getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
        } catch (Throwable ignored) {
        }

        boolean bridge = MethodCore.isAvailable();
        boolean sealed;
        try {
            String probe = "проверка";
            String s = Secrets.seal(probe);
            sealed = Secrets.isSealed(s) && probe.equals(Secrets.open(s));
        } catch (Throwable t) {
            sealed = false;
        }

        return row("Method " + version,
                (bridge ? "Общий слой загружен." : "ОБЩИЙ СЛОЙ НЕ ЗАГРУЗИЛСЯ — конфиг собрать нечем.")
                        + (sealed ? " Хранилище шифруется." : " ШИФРОВАНИЕ НЕДОСТУПНО.")
                        + "\n\nСпособы пройти считаются независимо: у каждого своя форма трафика, "
                        + "и то, что убивает один, не трогает остальные.",
                null, null, IconView.SHIELD, bridge && sealed ? Ui.ACCENT : Ui.DANGER);
    }

    private boolean batteryLimited() {
        try {
            PowerManager pm = getSystemService(PowerManager.class);
            return pm != null && !pm.isIgnoringBatteryOptimizations(getPackageName());
        } catch (Throwable t) {
            return false;
        }
    }

    private void askBattery() {
        try {
            startActivity(new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                    Uri.parse("package:" + getPackageName())));
        } catch (Throwable t) {
            toast("не вышло открыть настройки: " + t.getMessage());
        }
    }

    private void openVpnSettings() {
        try {
            startActivity(new Intent("android.net.vpn.SETTINGS"));
        } catch (Throwable t) {
            try {
                startActivity(new Intent(Settings.ACTION_SETTINGS));
            } catch (Throwable t2) {
                toast("не вышло открыть настройки: " + t2.getMessage());
            }
        }
    }

    private void confirmWipe() {
        new AlertDialog.Builder(this)
                .setTitle("Забыть всё?")
                .setMessage("Уйдут серверы, подписки, сценарии и накопленные замеры. "
                        + "Восстановить можно будет только из исходных ссылок.")
                .setPositiveButton("Забыть", (d, w) -> {
                    new Store(this).clear();
                    new PolicyStore(this).reset();
                    toast("всё забыто");
                })
                .setNegativeButton("Отмена", null)
                .show();
    }

    private void toast(String text) {
        android.widget.Toast.makeText(this, text, android.widget.Toast.LENGTH_LONG).show();
    }
}
