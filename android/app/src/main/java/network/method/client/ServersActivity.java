package network.method.client;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Typeface;
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
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Экран серверов.
 *
 * <p>Список сгруппирован по СПОСОБАМ ПРОЙТИ, а не по странам, и это не
 * оформительский выбор. Узел сам по себе не говорит почти ничего: на одном и
 * том же сервере США QUIC даёт 144 мс при джиттере 2, а Reality Vision — 420 мс
 * при джиттере 62 (замер по 117 кругам, см. брифинг). Втрое по задержке,
 * в тридцать раз по разбросу — на одном железе. Поэтому человек должен видеть
 * прежде всего, какими способами он вообще может пройти, а узлы — внутри них.
 *
 * <p>Пустые оси показываются тоже. Отсутствие способа — такой же факт, как его
 * наличие: пять живых осей и две живые оси это разная устойчивость, и увидеть
 * разницу человек должен здесь, а не когда его отрежут.
 *
 * <p>Отдельная страница, а не диалог: на Android список, который листают,
 * обязан закрываться системной кнопкой «назад». Диалог этого не даёт, и его
 * приходится закрывать нарисованной кнопкой — чужой для платформы приём.
 */
public final class ServersActivity extends Activity {

    private static final String TAG = "MethodServers";

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());

    private Store store;
    private LinearLayout column;
    private TextView summary;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        store = new Store(this);
        setContentView(buildUi());
        rebuild();
    }

    private View buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Ui.BG);

        int pad = Ui.dp(this, 20);

        // Шапка. Заголовок и одна строка сути — сколько узлов и сколькими
        // способами они ходят. Второе важнее первого.
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.VERTICAL);
        head.setPadding(pad, Ui.dp(this, 24), pad, Ui.dp(this, 12));
        Insets.pad(root);

        TextView title = new TextView(this);
        title.setText("Серверы");
        Ui.display(title);
        head.addView(title);

        summary = new TextView(this);
        Ui.caption(summary);
        summary.setPadding(0, Ui.dp(this, 4), 0, 0);
        head.addView(summary);
        root.addView(head);

        ScrollView scroll = new ScrollView(this);
        column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        column.setPadding(pad, 0, pad, Ui.dp(this, 12));
        scroll.addView(column);
        root.addView(scroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));

        root.addView(actions());
        return root;
    }

    private View actions() {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        int pad = Ui.dp(this, 20);
        row.setPadding(pad, Ui.dp(this, 14), pad, pad);
        // Волосяная линия сверху. Список длинный и уезжает под панель; без
        // границы последняя карточка выглядит обрезанной по случайности, а не
        // продолжающейся за край.
        row.setBackground(Deco.topHairline(this, Ui.BG, Ui.LINE));

        row.addView(button("Добавить", Ui.ACCENT, v -> askForSources()), weight());
        LinearLayout.LayoutParams right = weight();
        right.leftMargin = Ui.dp(this, 10);
        row.addView(button("Обновить", Ui.TEXT, v -> refreshSubscriptions()), right);
        return row;
    }

    private LinearLayout.LayoutParams weight() {
        return new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
    }

    private TextView button(String text, int color, View.OnClickListener action) {
        TextView b = new TextView(this);
        b.setText(text);
        Ui.action(b);
        b.setTextColor(color);
        b.setGravity(Gravity.CENTER);
        b.setPadding(0, Ui.dp(this, 14), 0, Ui.dp(this, 14));
        b.setBackground(Deco.pill(this, Ui.LEVEL_2, Ui.LINE));
        Deco.tappable(b, 100f);
        b.setOnClickListener(action);
        return b;
    }

    private void rebuild() {
        column.removeAllViews();
        int count = store.profileCount();

        if (count == 0) {
            column.addView(empty());
            summary.setText("пусто");
            return;
        }

        try {
            JSONObject inv = new JSONObject(MethodCore.inventory(store.profilesJson()));
            JSONArray axes = inv.getJSONArray("axes");
            int alive = 0;
            for (int i = 0; i < axes.length(); i++) {
                if (axes.getJSONObject(i).getJSONArray("nodes").length() > 0) alive++;
            }
            summary.setText(alive + " " + Ui.plural(alive, "способ", "способа", "способов")
                    + " пройти из " + axes.length()
                    + " · " + count + " " + Ui.plural(count, "узел", "узла", "узлов"));

            // Сперва ИСТОЧНИКИ — откуда узлы взялись и когда обновлялись, —
            // потом способы пройти. Человек, у которого что-то не работает,
            // первым делом спрашивает «а список-то свежий?».
            JSONArray sources = store.sources();
            for (int i = 0; i < sources.length(); i++) {
                column.addView(sourceCard(sources.getJSONObject(i)));
            }
            if (sources.length() > 0) column.addView(divider());

            for (int i = 0; i < axes.length(); i++) {
                column.addView(axisCard(axes.getJSONObject(i)));
            }
        } catch (Throwable t) {
            Log.e(TAG, "опись не читается: " + t);
            TextView error = new TextView(this);
            error.setText("список не читается: " + t.getMessage());
            error.setTextColor(Ui.DANGER);
            column.addView(error);
        }
    }

    /**
     * Карточка источника: подписка или ручные ссылки.
     *
     * <p>Подписка — живой список, за который отвечает выдавший её. Обновление
     * ЗАМЕНЯЕТ её узлы целиком, поэтому важно видеть, когда это было в
     * последний раз: список недельной давности и список пятиминутной давности
     * — разные вещи, а выглядят одинаково.
     */
    private View sourceCard(JSONObject src) throws Exception {
        boolean manual = "manual".equals(src.optString("id"));
        int nodes = src.optJSONArray("profiles") == null
                ? 0 : src.optJSONArray("profiles").length();

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.HORIZONTAL);
        card.setGravity(Gravity.CENTER_VERTICAL);
        card.setBackground(Deco.card(this, Ui.R_CARD, Ui.LEVEL_1, Ui.LINE));
        int pad = Ui.dp(this, 14);
        card.setPadding(pad, pad, pad, pad);

        LinearLayout text = new LinearLayout(this);
        text.setOrientation(LinearLayout.VERTICAL);

        TextView title = new TextView(this);
        title.setText(src.optString("title"));
        Ui.title(title);
        text.addView(title);

        TextView meta = new TextView(this);
        String count = nodes + " " + Ui.plural(nodes, "узел", "узла", "узлов");
        meta.setText(manual ? count + " · добавлены вручную"
                : count + " · обновлено " + ago(src.optLong("updatedAt")));
        Ui.label(meta);
        meta.setPadding(0, Ui.dp(this, 3), 0, 0);
        text.addView(meta);
        card.addView(text, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        if (!manual) {
            TextView refresh = new TextView(this);
            refresh.setText("обновить");
            Ui.label(refresh);
            refresh.setTextColor(Ui.ACCENT);
            refresh.setPadding(pad, Ui.dp(this, 9), pad, Ui.dp(this, 9));
            refresh.setBackground(Deco.pill(this, Ui.LEVEL_2, Ui.LINE));
            Deco.tappable(refresh, 100f);
            refresh.setOnClickListener(v -> {
                toast("обновляю…");
                worker.execute(() -> addFrom(src.optString("url")));
            });
            card.addView(refresh);
        }

        // Удаление — долгим нажатием. Кнопка рядом с «обновить» слишком легко
        // ловит промах, а источник уносит с собой все свои узлы.
        card.setOnLongClickListener(v -> {
            confirmRemoveSource(src.optString("id"), src.optString("title"), nodes);
            return true;
        });

        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Ui.dp(this, 8);
        card.setLayoutParams(lp);
        return card;
    }

    private void confirmRemoveSource(String id, String title, int nodes) {
        new AlertDialog.Builder(this)
                .setTitle("Удалить «" + title + "»?")
                .setMessage("Вместе с источником уйдёт " + nodes + " "
                        + Ui.plural(nodes, "узел", "узла", "узлов") + ".")
                .setPositiveButton("Удалить", (d, w) -> {
                    store.removeSource(id);
                    rebuild();
                })
                .setNegativeButton("Отмена", null)
                .show();
    }

    /** Давность по-человечески. Точная дата тут никому не нужна. */
    private static String ago(long at) {
        if (at <= 0) return "неизвестно когда";
        long min = Math.max(0, (System.currentTimeMillis() - at) / 60000);
        if (min < 1) return "только что";
        if (min < 60) return min + " мин назад";
        long hours = min / 60;
        if (hours < 24) return hours + " ч назад";
        return (hours / 24) + " дн назад";
    }

    private View divider() {
        View v = new View(this);
        v.setBackgroundColor(Ui.LINE);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 1);
        lp.topMargin = Ui.dp(this, 6);
        lp.bottomMargin = Ui.dp(this, 14);
        v.setLayoutParams(lp);
        return v;
    }

    /**
     * Пусто — это не ошибка и не забывчивость.
     *
     * <p>Клиент приезжает без единого сервера намеренно: он не наш сервис с
     * зашитыми узлами, а инструмент под свои. Сказать об этом прямо дешевле,
     * чем оставить человека гадать, почему список пуст.
     */
    private View empty() {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setBackground(Deco.card(this, Ui.R_CARD, Ui.LEVEL_1, Ui.LINE));
        int pad = Ui.dp(this, 18);
        box.setPadding(pad, pad, pad, pad);

        TextView head = new TextView(this);
        head.setText("Серверов нет");
        Ui.title(head);
        box.addView(head);

        TextView text = new TextView(this);
        text.setText("Клиент приезжает пустым — он работает с вашими серверами, "
                + "а не с чужими. Вставьте ссылки или адрес подписки.");
        Ui.caption(text);
        text.setPadding(0, Ui.dp(this, 8), 0, 0);
        box.addView(text);
        return box;
    }

    /**
     * Значок способа пройти.
     *
     * <p>У каждой оси своя форма, и это не декор: пять карточек с одинаковой
     * вёрсткой различаются только заголовком, а заголовки длинные и похожие
     * («gRPC в поддельном TLS» против «TCP в поддельном TLS»). Значок даёт
     * опознать нужную строку до чтения.
     */
    private static int axisIcon(String key) {
        switch (key) {
            case "quic-udp":     return IconView.BOLT;      // быстрый, поверх UDP
            case "fake-tls-h2":  return IconView.GLOBE;     // HTTP/2, форма веба
            case "fake-tls-tcp": return IconView.SHIELD;    // подделка под TLS
            case "real-tls":     return IconView.SHIELD_LOCK; // настоящий сертификат
            default:             return IconView.REFRESH;   // поток без рукопожатия
        }
    }

    private View axisCard(JSONObject axis) throws Exception {
        JSONArray nodes = axis.getJSONArray("nodes");
        boolean has = nodes.length() > 0;
        // Ось, по которой трафик идёт ПРЯМО СЕЙЧАС. Без этого экран показывает
        // пять равных вариантов, хотя работает один — и человек не может
        // связать то, что видит здесь, с тем, что написано на главном экране.
        boolean live = has && MethodVpnService.status == MethodVpnService.STATUS_CONNECTED
                && axis.getString("human").equals(MethodVpnService.currentPath);

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackground(live
                ? Deco.raised(this, Ui.R_CARD, Ui.LEVEL_2, Ui.LEVEL_1, Ui.alpha(Ui.ACCENT, 0.4f))
                : Deco.raised(this, Ui.R_CARD, Ui.LEVEL_2, Ui.LEVEL_1, Ui.LINE));
        int pad = Ui.dp(this, 16);
        card.setPadding(pad, pad, pad, has ? pad : Ui.dp(this, 14));

        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);

        IconView glyph = new IconView(this, axisIcon(axis.getString("key")),
                live ? Ui.ACCENT : has ? Ui.TEXT_2 : Ui.TEXT_3);
        int gs = Ui.dp(this, 17);
        LinearLayout.LayoutParams glp = new LinearLayout.LayoutParams(gs, gs);
        glp.rightMargin = Ui.dp(this, 11);
        head.addView(glyph, glp);

        TextView name = new TextView(this);
        name.setText(axis.getString("human"));
        Ui.title(name);
        // Погашенная ось не кричит: её отсутствие — сведение, а не тревога.
        name.setTextColor(has ? Ui.TEXT : Ui.TEXT_3);
        head.addView(name, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        TextView badge = new TextView(this);
        badge.setText(live ? "идём этим" : has ? String.valueOf(nodes.length()) : "нет");
        Ui.label(badge);
        badge.setTextColor(live ? Ui.ACCENT : has ? Ui.TEXT_2 : Ui.TEXT_3);
        head.addView(badge);
        card.addView(head);

        for (int i = 0; i < nodes.length(); i++) {
            card.addView(nodeRow(nodes.getJSONObject(i)));
        }

        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Ui.dp(this, 10);
        card.setLayoutParams(lp);
        return card;
    }

    private View nodeRow(JSONObject node) throws Exception {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(0, Ui.dp(this, 12), 0, 0);

        TextView name = new TextView(this);
        name.setText(node.getString("name"));
        Ui.body(name);
        name.setSingleLine(true);
        name.setEllipsize(android.text.TextUtils.TruncateAt.END);
        box.addView(name);

        // Адрес и протокол — одной строкой, приглушённо. Это справка для
        // разбирательства, а не то, что читают каждый день.
        //
        // Протокол не повторяем, если он уже назван в имени узла: подписки
        // почти всегда пишут его туда («NOT_A_RKN-LT · Hysteria2»), и строка
        // «Hysteria2 · Hysteria2 · адрес» — это шум, за которым
        // теряется единственное, чего в имени нет: адрес.
        String protocol = node.getString("protocol");
        String meta_text = node.getString("host") + ":" + node.getInt("port");
        if (!namesProtocol(node.getString("name"), protocol)) {
            meta_text = protocol + " · " + meta_text;
        }
        TextView meta = new TextView(this);
        meta.setText(meta_text);
        Ui.label(meta);
        meta.setTypeface(Ui.MONO);
        meta.setPadding(0, Ui.dp(this, 2), 0, 0);
        box.addView(meta);
        return box;
    }

    /**
     * Названо ли уже в имени узла то же самое, что мы собираемся приписать.
     *
     * <p>Сверяем по ПЕРВОМУ слову протокола: в имени пишут «Hysteria2», а у нас
     * «VLESS + Reality · gRPC» — совпадения целиком не будет никогда.
     */
    private static boolean namesProtocol(String name, String protocol) {
        String head = protocol.split("[ ·+]")[0].trim().toLowerCase();
        return !head.isEmpty() && name.toLowerCase().contains(head);
    }

    private void askForSources() {
        EditText field = new EditText(this);
        field.setHint("ссылки или адрес подписки");
        field.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        field.setMinLines(4);
        field.setGravity(Gravity.TOP);

        new AlertDialog.Builder(this)
                .setTitle("Свои серверы")
                // Объяснение здесь обязательно: это первый экран, на который
                // человек попадает с пустым клиентом, и голое поле ввода не
                // отвечает ни на один из вопросов, которые у него есть.
                .setMessage("Клиент работает с вашими серверами, а не с чужими — "
                        + "поэтому приезжает пустым.\n\nВставьте ссылки вида "
                        + "hysteria2://, vless://, trojan:// или ss:// — по одной "
                        + "в строке. Либо адрес подписки: тогда список будет "
                        + "обновляться сам.")
                .setView(field)
                .setPositiveButton("Добавить", (d, w) -> {
                    String text = field.getText().toString().trim();
                    if (!text.isEmpty()) worker.execute(() -> addFrom(text));
                })
                .setNeutralButton("Забыть все", (d, w) -> confirmForget())
                .setNegativeButton("Отмена", null)
                .show();
    }

    /**
     * «Забыть все» спрашивает подтверждения.
     *
     * <p>Действие необратимо и стоит человеку подписки, которую он, возможно,
     * не сможет достать повторно. Кнопка без вопроса стоит рядом с «Добавить»
     * и промахнуться по ней легко.
     */
    private void confirmForget() {
        new AlertDialog.Builder(this)
                .setTitle("Забыть все серверы?")
                .setMessage("Ссылки и адрес подписки будут стёрты. Восстановить их "
                        + "можно будет только из исходного источника.")
                .setPositiveButton("Забыть", (d, w) -> {
                    store.clear();
                    rebuild();
                    toast("серверы забыты");
                })
                .setNegativeButton("Отмена", null)
                .show();
    }

    /**
     * Перезабрать все подписки.
     *
     * <p>Подписок может быть несколько, и обновлять надо все: узел, пропавший
     * из одной, мог переехать в другую. Отказ одной не отменяет остальные —
     * иначе одна недоступная ссылка лишала бы человека обновления целиком.
     */
    private void refreshSubscriptions() {
        java.util.List<String> urls = store.subscriptions();
        if (urls.isEmpty()) {
            toast("подписка не задана — добавьте её адрес");
            return;
        }
        toast("обновляю…");
        worker.execute(() -> {
            int added = 0, failed = 0;
            for (String url : urls) {
                try {
                    Store.Result r = store.add(Subscriptions.fetch(url), url);
                    if (r.ok) added += r.added; else failed++;
                } catch (Throwable t) {
                    Log.w(TAG, "подписка не забралась (" + url + "): " + t);
                    failed++;
                }
            }
            toast(failed == 0
                    ? "обновлено, узлов: " + added
                    : "обновлено, узлов: " + added + "; не забралось подписок: " + failed);
            ui.post(this::rebuild);
        });
    }

    private void addFrom(String text) {
        try {
            String payload = text;
            String source = null;
            if (text.startsWith("http://") || text.startsWith("https://")) {
                payload = Subscriptions.fetch(text);
                store.rememberSubscription(text);
                source = text;
            }
            Store.Result r = store.add(payload, source);
            toast(r.ok
                    ? "добавлено " + r.added + " "
                        + Ui.plural(r.added, "узел", "узла", "узлов")
                        + (r.rejected > 0 ? ", негодных строк: " + r.rejected : "")
                    : "не вышло: " + r.message);
            ui.post(this::rebuild);
        } catch (Throwable t) {
            toast("ошибка: " + t.getMessage());
        }
    }

    private void toast(String text) {
        Log.i(TAG, text);
        ui.post(() -> android.widget.Toast.makeText(this, text,
                android.widget.Toast.LENGTH_LONG).show());
    }
}
