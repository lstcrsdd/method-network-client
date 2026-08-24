package network.method.client;

import android.app.AlertDialog;
import android.content.Context;
import android.graphics.Paint;
import android.graphics.Typeface;
import android.text.InputType;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.ArrayAdapter;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Экран сценариев: какой трафик каким путём идёт.
 *
 * <p>Правило называет ПОЛОСУ, а не сервер. «Банк — напрямую», «всё остальное —
 * через туннель»: какой маршрут сегодня удовлетворяет требованию, решает
 * движок. Поэтому правила не приходится переписывать при смене узлов.
 *
 * <p>Перекрытые правила показываются перечёркнутыми. Это не украшение: ядро
 * берёт первое совпавшее правило сверху вниз, и правило, значения которого
 * перечислены выше, не срабатывает никогда — а в списке выглядит действующим.
 * На macOS это стоило вечера поисков, пока не выяснилось, что старое правило
 * «2ip.io → весь трафик» перекрывает новое «2ip.io → мимо туннеля».
 */
final class ScenariosView extends ScrollView {

    interface Callbacks {
        /** Правила изменены — конфиг ядра собирается при подключении. */
        void onPolicyChanged();
    }

    private final PolicyStore policies;
    private final Callbacks callbacks;
    private final LinearLayout column;

    ScenariosView(Context ctx, PolicyStore policies, Callbacks callbacks) {
        super(ctx);
        this.policies = policies;
        this.callbacks = callbacks;
        setBackgroundColor(Ui.BG);

        column = new LinearLayout(ctx);
        column.setOrientation(LinearLayout.VERTICAL);
        int pad = Ui.dp(ctx, 20);
        column.setPadding(pad, pad, pad, pad);
        Insets.pad(column);
        addView(column);
        rebuild();
    }

    void rebuild() {
        Context ctx = getContext();
        column.removeAllViews();

        TextView title = new TextView(ctx);
        title.setText("Сценарии");
        Ui.display(title);
        column.addView(title);

        TextView sub = new TextView(ctx);
        sub.setText("Правило называет путь, а не сервер. Какой маршрут ему сегодня "
                + "соответствует — решает клиент сам.");
        Ui.caption(sub);
        sub.setPadding(0, Ui.dp(ctx, 6), 0, Ui.dp(ctx, 16));
        column.addView(sub);

        try {
            JSONObject policy = policies.load();
            Map<Integer, Integer> shadowed = shadowedMap(policy);
            JSONArray lanes = policy.getJSONArray("lanes");
            JSONArray flows = policy.getJSONArray("flows");

            for (int i = 0; i < lanes.length(); i++) {
                column.addView(laneCard(lanes.getJSONObject(i), flows, shadowed));
            }
        } catch (Throwable t) {
            TextView error = new TextView(ctx);
            error.setText("сценарии не читаются: " + t);
            error.setTextColor(Ui.DANGER);
            column.addView(error);
        }

        TextView add = new TextView(ctx);
        add.setText("Добавить правило");
        Ui.action(add);
        add.setTextColor(Ui.ACCENT);
        add.setGravity(Gravity.CENTER);
        add.setPadding(0, Ui.dp(ctx, 14), 0, Ui.dp(ctx, 14));
        add.setBackground(Deco.pill(ctx, Ui.LEVEL_2, Ui.LINE));
        Deco.tappable(add, 100f);
        add.setOnClickListener(v -> askForRule());
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.topMargin = Ui.dp(ctx, 12);
        column.addView(add, lp);
    }

    /** Индекс перекрытого правила → индекс того, кто его перекрыл. */
    private Map<Integer, Integer> shadowedMap(JSONObject policy) {
        Map<Integer, Integer> out = new HashMap<>();
        try {
            JSONObject report = new JSONObject(MethodCore.policyReport(
                    policy.toString(), new Store(getContext()).profilesJson()));
            JSONArray list = report.optJSONArray("shadowed");
            for (int i = 0; list != null && i < list.length(); i++) {
                JSONObject s = list.getJSONObject(i);
                out.put(s.optInt("index"), s.optInt("by"));
            }
        } catch (Throwable ignored) {
        }
        return out;
    }

    private View laneCard(JSONObject lane, JSONArray flows, Map<Integer, Integer> shadowed)
            throws Exception {
        Context ctx = getContext();
        boolean direct = lane.optBoolean("allowsDirect");

        LinearLayout card = new LinearLayout(ctx);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackground(Deco.card(ctx, Ui.R_CARD, Ui.LEVEL_1, Ui.LINE));
        int pad = Ui.dp(ctx, 14);
        card.setPadding(pad, pad, pad, pad);

        LinearLayout head = new LinearLayout(ctx);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);

        TextView name = new TextView(ctx);
        name.setText(lane.getString("title"));
        Ui.title(name);
        head.addView(name, new LinearLayout.LayoutParams(0,
                LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        TextView mode = new TextView(ctx);
        mode.setText(direct ? "мимо туннеля" : "через туннель");
        Ui.label(mode);
        mode.setTextColor(direct ? Ui.WARN : Ui.ACCENT);
        head.addView(mode);
        card.addView(head);

        // Объяснение показывается ВСЕГДА, когда полоса выпускает трафик
        // открытым. Через месяц никто не вспомнит, почему банк ходит мимо
        // туннеля.
        String why = lane.optString("justification", "");
        if (direct && !why.isEmpty()) {
            TextView reason = new TextView(ctx);
            reason.setText(why);
            Ui.label(reason);
            reason.setTextColor(Ui.WARN);
            reason.setPadding(0, Ui.dp(ctx, 6), 0, 0);
            card.addView(reason);
        }

        String laneId = lane.getString("id");
        for (int i = 0; i < flows.length(); i++) {
            JSONObject flow = flows.getJSONObject(i);
            if (!laneId.equals(flow.optString("lane"))) continue;
            card.addView(ruleRow(flow, i, shadowed.get(i), flows));
        }

        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Ui.dp(ctx, 12);
        card.setLayoutParams(lp);
        return card;
    }

    private View ruleRow(JSONObject flow, int index, Integer coveredBy, JSONArray flows)
            throws Exception {
        Context ctx = getContext();
        LinearLayout box = new LinearLayout(ctx);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(0, Ui.dp(ctx, 10), 0, 0);

        TextView text = new TextView(ctx);
        text.setText(describe(flow));
        text.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12);
        if (coveredBy != null) {
            text.setTextColor(Ui.TEXT_3);
            text.setPaintFlags(text.getPaintFlags() | Paint.STRIKE_THRU_TEXT_FLAG);
        } else {
            text.setTextColor(Ui.TEXT_2);
        }
        box.addView(text);

        if (coveredBy != null) {
            TextView note = new TextView(ctx);
            note.setText("не сработает: выше уже стоит «"
                    + describe(flows.getJSONObject(coveredBy)) + "»");
            note.setTextColor(Ui.WARN);
            note.setTextSize(TypedValue.COMPLEX_UNIT_SP, 10);
            box.addView(note);
        }

        box.setOnLongClickListener(v -> {
            removeRule(index);
            return true;
        });
        return box;
    }

    private String describe(JSONObject flow) {
        String kind = flow.optString("kind");
        JSONArray values = flow.optJSONArray("values");
        List<String> list = new ArrayList<>();
        for (int i = 0; values != null && i < values.length(); i++) {
            list.add(values.optString(i));
        }
        String joined = String.join(", ", list);
        switch (kind) {
            case "domainSuffix": return "сайты: " + joined;
            case "domainKeyword": return "в адресе есть: " + joined;
            case "processName": return "приложения: " + joined;
            case "port": return "порты: " + joined;
            case "private": return "локальная сеть";
            default: return kind + ": " + joined;
        }
    }

    private void removeRule(int index) {
        try {
            JSONObject policy = policies.load();
            JSONArray flows = policy.getJSONArray("flows");
            JSONArray kept = new JSONArray();
            for (int i = 0; i < flows.length(); i++) {
                if (i != index) kept.put(flows.get(i));
            }
            policy.put("flows", kept);
            policies.save(policy);
            rebuild();
            callbacks.onPolicyChanged();
        } catch (Throwable ignored) {
        }
    }

    private void askForRule() {
        Context ctx = getContext();
        LinearLayout box = new LinearLayout(ctx);
        box.setOrientation(LinearLayout.VERTICAL);
        int pad = Ui.dp(ctx, 16);
        box.setPadding(pad, pad, pad, 0);

        EditText domains = new EditText(ctx);
        domains.setHint("например: tinkoff.ru, sberbank.ru");
        domains.setInputType(InputType.TYPE_CLASS_TEXT);
        box.addView(domains);

        List<String> laneIds = new ArrayList<>();
        List<String> laneTitles = new ArrayList<>();
        try {
            JSONArray lanes = policies.load().getJSONArray("lanes");
            for (int i = 0; i < lanes.length(); i++) {
                JSONObject l = lanes.getJSONObject(i);
                laneIds.add(l.getString("id"));
                laneTitles.add(l.getString("title")
                        + (l.optBoolean("allowsDirect") ? " — мимо туннеля" : ""));
            }
        } catch (Throwable ignored) {
        }

        Spinner lanePicker = new Spinner(ctx);
        lanePicker.setAdapter(new ArrayAdapter<>(ctx,
                android.R.layout.simple_spinner_dropdown_item, laneTitles));
        box.addView(lanePicker);

        // Объяснение обязательно только для прямого выхода: там цена ошибки —
        // раскрытый адрес, и через месяц никто не вспомнит, зачем это сделано.
        EditText why = new EditText(ctx);
        why.setHint("зачем мимо туннеля (для прямого выхода обязательно)");
        why.setInputType(InputType.TYPE_CLASS_TEXT);
        box.addView(why);

        new AlertDialog.Builder(ctx)
                .setTitle("Новое правило")
                .setView(box)
                .setPositiveButton("Добавить", (d, w) -> {
                    String raw = domains.getText().toString().trim();
                    if (raw.isEmpty()) return;
                    String[] list = raw.split("\\s*,\\s*");
                    int pos = lanePicker.getSelectedItemPosition();
                    if (pos < 0 || pos >= laneIds.size()) return;
                    String problem = policies.addDomainRule(
                            list, laneIds.get(pos), why.getText().toString());
                    if (problem != null) {
                        new AlertDialog.Builder(ctx)
                                .setTitle("Правило не принято")
                                .setMessage(problem)
                                .setPositiveButton("Понятно", null)
                                .show();
                        return;
                    }
                    rebuild();
                    callbacks.onPolicyChanged();
                })
                .setNegativeButton("Отмена", null)
                .show();
    }
}
