package network.method.client;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.TextWatcher;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Раздельная маршрутизация по приложениям.
 *
 * <p>Отличие от сценариев принципиальное, и человек должен его понимать.
 * Сценарии решают, каким ПУТЁМ идёт трафик, уже попавший в туннель. Здесь
 * решается, попадёт ли он туда вообще: выведенное отсюда приложение ходит
 * открытым, со своим настоящим адресом, и никакая полоса его не прикроет.
 *
 * <p>В списке только те приложения, которые вообще умеют ходить в сеть.
 * Остальные показывать бессмысленно: правило для калькулятора без доступа в
 * интернет ничего не меняет, а список раздувает втрое.
 */
public final class AppsActivity extends Activity {

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());

    private AppRules rules;
    private LinearLayout list;
    private TextView modeLine;
    private String filter = "";
    private List<Entry> apps = new ArrayList<>();
    /** Отличает «ещё читаем» от «прочитали, и там пусто». */
    private volatile boolean loaded;

    /** Приложение в списке. Имя держим рядом: сортировка идёт по нему. */
    private static final class Entry {
        final String pkg;
        final String label;
        final Drawable icon;
        final boolean system;

        Entry(String pkg, String label, Drawable icon, boolean system) {
            this.pkg = pkg;
            this.label = label;
            this.icon = icon;
            this.system = system;
        }
    }

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        rules = new AppRules(this);
        setContentView(buildUi());
        // Перебрать установленные приложения и вытащить иконки — работа на
        // сотни миллисекунд. В главном потоке это заметная задержка при каждом
        // открытии экрана.
        worker.execute(this::loadApps);
    }

    private View buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Ui.BG);
        int pad = Ui.dp(this, 20);

        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.VERTICAL);
        head.setPadding(pad, Ui.dp(this, 24), pad, Ui.dp(this, 10));
        Insets.pad(root);

        TextView title = new TextView(this);
        title.setText("Приложения");
        Ui.display(title);
        head.addView(title);

        modeLine = new TextView(this);
        Ui.caption(modeLine);
        modeLine.setPadding(0, Ui.dp(this, 4), 0, Ui.dp(this, 12));
        head.addView(modeLine);

        // Сброс — рядом с режимами и только когда есть что сбрасывать. Снимать
        // отметки по одной, чтобы вернуться к «всё через туннель», — работа,
        // которую человек не должен делать руками.
        if (!rules.isEmpty()) {
            TextView reset = new TextView(this);
            reset.setText("Снять все отметки");
            Ui.label(reset);
            reset.setTextColor(Ui.WARN);
            reset.setPadding(0, 0, 0, Ui.dp(this, 12));
            reset.setOnClickListener(v -> {
                rules.clear();
                recreateHeader();
            });
            head.addView(reset);
        }

        head.addView(modeSwitch());

        EditText search = new EditText(this);
        search.setHint("поиск");
        search.setTextColor(Ui.TEXT);
        search.setHintTextColor(Ui.TEXT_3);
        search.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        search.setTypeface(Ui.REGULAR);
        search.setSingleLine(true);
        search.setBackground(Deco.pill(this, Ui.LEVEL_1, Ui.LINE));
        int sp = Ui.dp(this, 14);
        search.setPadding(sp, sp, sp, sp);
        // Лупа внутри поля: пустое поле с одним словом «поиск» опознаётся
        // чтением, со значком — с одного взгляда.
        android.graphics.drawable.Drawable lens = new IconDrawable(this, IconView.SEARCH,
                Ui.TEXT_3, Ui.dp(this, 17));
        search.setCompoundDrawables(lens, null, null, null);
        search.setCompoundDrawablePadding(Ui.dp(this, 10));
        search.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int a, int b, int c) {}
            @Override public void onTextChanged(CharSequence s, int a, int b, int c) {}
            @Override public void afterTextChanged(Editable e) {
                filter = e.toString().trim().toLowerCase();
                renderList();
            }
        });
        LinearLayout.LayoutParams slp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        slp.topMargin = Ui.dp(this, 12);
        head.addView(search, slp);
        root.addView(head);

        ScrollView scroll = new ScrollView(this);
        list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        list.setPadding(pad, 0, pad, pad);
        scroll.addView(list);
        root.addView(scroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));

        showSkeleton();

        updateModeLine();
        return root;
    }

    /**
     * Переключатель режима.
     *
     * <p>Два режима стоят рядом, и выбранный подсвечен — так видно, что это
     * выбор из двух, а не галочка. «Только выбранные» помечен предупреждением:
     * в нём всё, о чём человек не подумал, идёт открытым, включая приложения,
     * установленные завтра.
     */
    private View modeSwitch() {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.addView(modeButton("Все, кроме выбранных", AppRules.MODE_EXCEPT), half(0));
        row.addView(modeButton("Только выбранные", AppRules.MODE_ONLY), half(Ui.dp(this, 8)));
        return row;
    }

    private LinearLayout.LayoutParams half(int left) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        lp.leftMargin = left;
        return lp;
    }

    private TextView modeButton(String text, String mode) {
        boolean active = mode.equals(rules.mode());
        TextView b = new TextView(this);
        b.setText(text);
        Ui.label(b);
        b.setTextColor(active ? Ui.TEXT : Ui.TEXT_3);
        if (active) b.setTypeface(Ui.SEMIBOLD);
        b.setGravity(Gravity.CENTER);
        b.setPadding(0, Ui.dp(this, 12), 0, Ui.dp(this, 12));
        // Выбранный режим — приподнят и с акцентной рамкой: без неё два
        // одинаковых прямоугольника не читаются как выбор из двух.
        b.setBackground(Deco.pill(this, active ? Ui.LEVEL_2 : Ui.LEVEL_1,
                active ? Ui.alpha(Ui.ACCENT, 0.35f) : Ui.LINE));
        Deco.tappable(b, 100f);
        b.setOnClickListener(v -> {
            if (AppRules.MODE_ONLY.equals(mode) && !mode.equals(rules.mode())) {
                warnOnlyMode();
                return;
            }
            rules.setMode(mode);
            recreateHeader();
        });
        return b;
    }

    private void warnOnlyMode() {
        new AlertDialog.Builder(this)
                .setTitle("Только выбранные?")
                .setMessage("В этом режиме через туннель пойдут ТОЛЬКО отмеченные "
                        + "приложения. Все остальные — открытым, со своим настоящим "
                        + "адресом, включая те, что вы установите завтра.")
                .setPositiveButton("Понимаю", (d, w) -> {
                    rules.setMode(AppRules.MODE_ONLY);
                    recreateHeader();
                })
                .setNegativeButton("Отмена", null)
                .show();
    }

    private void recreateHeader() {
        setContentView(buildUi());
        renderList();
        warnReconnect();
    }

    private void updateModeLine() {
        modeLine.setText(rules.summary()
                + "\nПравила вступят в силу при следующем подключении.");
    }

    /**
     * Отметка выбора.
     *
     * <p>Кружок вместо подписи «в туннеле» у каждой строки. Триста одинаковых
     * подписей — это не информация, а фон: глаз перестаёт их читать на пятой
     * строке. Отметка же считывается формой, и в списке сразу видно, сколько
     * их и где они.
     *
     * <p>Цвет отметки — цвет ПОСЛЕДСТВИЯ, а не выбора: оранжевый там, где
     * приложение уходит открытым, зелёный там, где остаётся под защитой.
     */
    private View checkMark(boolean on, boolean tunnelled) {
        FrameLayout box = new FrameLayout(this);
        int size = Ui.dp(this, 24);
        int color = tunnelled ? Ui.ACCENT : Ui.WARN;
        box.setBackground(Deco.pill(this,
                on ? Ui.alpha(color, 0.18f) : android.graphics.Color.TRANSPARENT,
                on ? Ui.alpha(color, 0.55f) : Ui.LINE));
        if (on) {
            IconView tick = new IconView(this, IconView.CHECK, color);
            int gs = Ui.dp(this, 13);
            FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(gs, gs);
            lp.gravity = Gravity.CENTER;
            box.addView(tick, lp);
        }
        box.setLayoutParams(new LinearLayout.LayoutParams(size, size));
        return box;
    }

    /**
     * Список приложений, умеющих ходить в сеть.
     *
     * <p>Разрешения спрашиваем по одному, а не запросом «дай все пакеты вместе
     * с разрешениями». Такой запрос кладёт в один межпроцессный ответ разом всё
     * — на телефоне с четырьмя сотнями приложений это пробивает лимит в
     * мегабайт, и вызов падает целиком. Проверено здесь же: список молча
     * оставался пустым при выданном разрешении и 440 установленных пакетах.
     */
    private void loadApps() {
        List<Entry> found = new ArrayList<>();
        try {
            PackageManager pm = getPackageManager();
            List<ApplicationInfo> installed = pm.getInstalledApplications(0);
            for (ApplicationInfo info : installed) {
                if (info.packageName == null) continue;
                if (getPackageName().equals(info.packageName)) continue;
                if (pm.checkPermission("android.permission.INTERNET", info.packageName)
                        != PackageManager.PERMISSION_GRANTED) {
                    continue;
                }
                boolean system = (info.flags & ApplicationInfo.FLAG_SYSTEM) != 0;
                String label = String.valueOf(pm.getApplicationLabel(info));
                Drawable icon;
                try {
                    icon = pm.getApplicationIcon(info);
                } catch (Throwable t) {
                    icon = null;
                }
                found.add(new Entry(info.packageName, label, icon, system));
            }
            Collections.sort(found, (a, b) -> a.label.compareToIgnoreCase(b.label));
        } catch (Throwable t) {
            // Молча проглотить эту ошибку значит показать человеку пустой
            // экран без единой подсказки, что пошло не так.
            android.util.Log.e("MethodApps", "список приложений не прочитан: " + t);
        }
        apps = found;
        loaded = true;
        ui.post(this::renderList);
    }

    private void renderList() {
        list.removeAllViews();
        updateModeLine();

        Set<String> marked = rules.packages();
        int shown = 0;

        // Три группы в осмысленном порядке: отмеченные, свои, системные.
        // Системные — три четверти списка, и человеку они нужны в последнюю
        // очередь: он ищет мессенджер или банк, а не «Android Services Library».
        shown += fill(marked, 0, null);
        if (shown > 0) list.addView(gap());
        int own = fill(marked, 1, "Ваши приложения");
        int sys = fill(marked, 2, "Системные");
        shown += own + sys;

        if (shown == 0) {
            TextView empty = new TextView(this);
            if (!loaded) {
                showSkeleton();
                return;
            }
            empty.setText(apps.isEmpty()
                    ? "не удалось прочитать список приложений"
                    : "ничего не найдено");
            Ui.caption(empty);
            list.addView(empty);
        }
    }

    /**
     * Заполнить группу.
     *
     * @param group 0 — отмеченные, 1 — свои, 2 — системные
     * @return сколько строк добавлено
     */
    private int fill(Set<String> marked, int group, String heading) {
        List<View> rows = new ArrayList<>();
        for (Entry e : apps) {
            boolean on = marked.contains(e.pkg);
            if (group == 0 && !on) continue;
            if (group == 1 && (on || e.system)) continue;
            if (group == 2 && (on || !e.system)) continue;
            if (!filter.isEmpty() && !e.label.toLowerCase().contains(filter)
                    && !e.pkg.toLowerCase().contains(filter)) {
                continue;
            }
            rows.add(appRow(e, on));
        }
        if (rows.isEmpty()) return 0;
        if (heading != null) {
            TextView h = new TextView(this);
            h.setText(heading);
            Ui.section(h);
            h.setPadding(Ui.dp(this, 4), Ui.dp(this, 14), 0, Ui.dp(this, 8));
            list.addView(h);
        }
        for (View v : rows) list.addView(v);
        return rows.size();
    }

    /**
     * Заглушки на время чтения списка.
     *
     * <p>Перебрать четыре сотни приложений с иконками — это секунды, и всё это
     * время экран показывал одну серую строку на чёрном: выглядит как пустой
     * или сломанный. Заглушки той же формы, что будущие строки, говорят
     * «список сейчас будет» без единого слова и заранее показывают его вид.
     */
    private void showSkeleton() {
        for (int i = 0; i < 7; i++) {
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setBackground(Deco.card(this, Ui.R_ROW, Ui.LEVEL_1, Ui.LINE));
            int pad = Ui.dp(this, 12);
            row.setPadding(pad, pad, pad, pad);

            View icon = new View(this);
            icon.setBackground(Deco.card(this, 10f, Ui.LEVEL_2, android.graphics.Color.TRANSPARENT));
            int size = Ui.dp(this, 32);
            row.addView(icon, new LinearLayout.LayoutParams(size, size));

            LinearLayout bars = new LinearLayout(this);
            bars.setOrientation(LinearLayout.VERTICAL);
            LinearLayout.LayoutParams blp = new LinearLayout.LayoutParams(0,
                    LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
            blp.leftMargin = Ui.dp(this, 12);

            // Разная длина полосок: одинаковые читаются как таблица, а не как
            // ещё не пришедшие названия.
            bars.addView(bar(Ui.dp(this, 96 + (i * 23) % 60), Ui.dp(this, 11)));
            View gapView = new View(this);
            bars.addView(gapView, new LinearLayout.LayoutParams(1, Ui.dp(this, 6)));
            bars.addView(bar(Ui.dp(this, 130 + (i * 17) % 70), Ui.dp(this, 8)));
            row.addView(bars, blp);

            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
            lp.bottomMargin = Ui.dp(this, 6);
            row.setLayoutParams(lp);
            list.addView(row);
        }
    }

    private View bar(int width, int height) {
        View v = new View(this);
        v.setBackground(Deco.pill(this, Ui.LEVEL_2, android.graphics.Color.TRANSPARENT));
        v.setLayoutParams(new LinearLayout.LayoutParams(width, height));
        return v;
    }

    private View gap() {
        View v = new View(this);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, Ui.dp(this, 10));
        v.setLayoutParams(lp);
        return v;
    }

    private View appRow(Entry e, boolean on) {
        boolean only = AppRules.MODE_ONLY.equals(rules.mode());

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        // Отмеченная строка приподнята и обведена цветом своего состояния:
        // «мимо туннеля» предупреждает, «в туннеле» подтверждает.
        int edge = on ? Ui.alpha(only ? Ui.ACCENT : Ui.WARN, 0.35f) : Ui.LINE;
        row.setBackground(Deco.card(this, Ui.R_ROW, on ? Ui.LEVEL_2 : Ui.LEVEL_1, edge));
        Deco.tappable(row, Ui.R_ROW);
        int pad = Ui.dp(this, 12);
        row.setPadding(pad, pad, pad, pad);

        ImageView icon = new ImageView(this);
        icon.setImageDrawable(e.icon);
        int size = Ui.dp(this, 32);
        row.addView(icon, new LinearLayout.LayoutParams(size, size));

        LinearLayout text = new LinearLayout(this);
        text.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams tlp = new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        tlp.leftMargin = Ui.dp(this, 12);

        TextView name = new TextView(this);
        name.setText(e.label);
        Ui.body(name);
        name.setTextColor(Ui.TEXT);
        name.setSingleLine(true);
        name.setEllipsize(android.text.TextUtils.TruncateAt.END);
        text.addView(name);

        TextView meta = new TextView(this);
        meta.setText(e.system ? e.pkg + " · системное" : e.pkg);
        Ui.label(meta);
        meta.setTextSize(TypedValue.COMPLEX_UNIT_SP, 10);
        meta.setSingleLine(true);
        // Длинные имена пакетов обрезаем многоточием, иначе они наезжают на
        // состояние справа и обе надписи становятся нечитаемыми.
        meta.setEllipsize(android.text.TextUtils.TruncateAt.END);
        text.addView(meta);
        row.addView(text, tlp);

        boolean tunnelled = on == only;
        row.addView(checkMark(on, tunnelled));

        row.setOnClickListener(v -> {
            rules.toggle(e.pkg);
            renderList();
            warnReconnect();
        });

        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Ui.dp(this, 6);
        row.setLayoutParams(lp);
        return row;
    }

    /**
     * Напоминание о переподключении.
     *
     * <p>Состав туннеля задаётся при его СОЗДАНИИ: у Android нет способа
     * переселить приложение в уже поднятый туннель. Молча сохранить правило и
     * не применить его хуже, чем отказать: список говорит «сделано» при
     * прежнем поведении.
     */
    private boolean warned;

    private void warnReconnect() {
        if (warned) return;
        if (MethodVpnService.status != MethodVpnService.STATUS_CONNECTED) return;
        warned = true;
        android.widget.Toast.makeText(this,
                "правила изменены — переподключитесь, чтобы они заработали",
                android.widget.Toast.LENGTH_LONG).show();
    }
}
