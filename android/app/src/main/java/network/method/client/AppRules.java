package network.method.client;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.LinkedHashSet;
import java.util.Set;

/**
 * Какие приложения идут через туннель, а какие мимо.
 *
 * <p>Это НЕ сценарии. Сценарии решают, каким путём идёт трафик, уже попавший в
 * туннель; здесь решается, попадёт ли он туда вообще. Разница принципиальная:
 * приложение, выведенное отсюда, ходит открытым — со своим настоящим адресом,
 * и никакая полоса его уже не прикроет.
 *
 * <p>Два режима, и смешивать их нельзя — этого не позволяет и сам Android.
 * «Все, кроме выбранных» — привычный и безопасный по умолчанию: новое
 * установленное приложение автоматически окажется под защитой. «Только
 * выбранные» — строже и опаснее: всё, о чём человек не подумал, пойдёт мимо
 * туннеля, включая приложения, установленные завтра.
 *
 * <p>Шифруется вместе с остальным: список приложений, которые человек выводит
 * из туннеля, — это рассказ о том, чем он пользуется и что скрывает.
 */
final class AppRules {

    private static final String TAG = "MethodApps";
    private static final String FILE = "method-apps";
    private static final String KEY = "rules";

    /** Все приложения в туннеле, кроме перечисленных. */
    static final String MODE_EXCEPT = "except";
    /** В туннеле только перечисленные. */
    static final String MODE_ONLY = "only";

    private final SharedPreferences prefs;

    AppRules(Context context) {
        prefs = context.getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    private JSONObject load() {
        try {
            String raw = prefs.getString(KEY, "");
            if (!raw.isEmpty()) return new JSONObject(Secrets.open(raw));
        } catch (Throwable t) {
            Log.e(TAG, "правила приложений не читаются, берём заводские: " + t);
        }
        try {
            return new JSONObject().put("mode", MODE_EXCEPT).put("packages", new JSONArray());
        } catch (Throwable t) {
            return new JSONObject();
        }
    }

    private void save(JSONObject o) {
        try {
            prefs.edit().putString(KEY, Secrets.seal(o.toString())).apply();
        } catch (Throwable t) {
            Log.e(TAG, "правила приложений не записаны: " + t);
        }
    }

    String mode() {
        String m = load().optString("mode", MODE_EXCEPT);
        return MODE_ONLY.equals(m) ? MODE_ONLY : MODE_EXCEPT;
    }

    void setMode(String mode) {
        JSONObject o = load();
        try {
            o.put("mode", MODE_ONLY.equals(mode) ? MODE_ONLY : MODE_EXCEPT);
            save(o);
        } catch (Throwable ignored) {
        }
    }

    /** Отмеченные пакеты. Что это значит — зависит от режима. */
    Set<String> packages() {
        Set<String> out = new LinkedHashSet<>();
        JSONArray a = load().optJSONArray("packages");
        for (int i = 0; a != null && i < a.length(); i++) {
            String p = a.optString(i, "");
            if (!p.isEmpty()) out.add(p);
        }
        return out;
    }

    void toggle(String pkg) {
        Set<String> have = packages();
        if (!have.remove(pkg)) have.add(pkg);
        JSONObject o = load();
        try {
            o.put("packages", new JSONArray(have));
            save(o);
        } catch (Throwable ignored) {
        }
    }

    void clear() {
        prefs.edit().remove(KEY).apply();
    }

    /** Склонение при числе. Дубль ради того, чтобы модель не зависела от слоя вида. */
    private static String plural(int n, String one, String few, String many) {
        int abs = Math.abs(n) % 100;
        if (abs >= 11 && abs <= 14) return many;
        switch (abs % 10) {
            case 1: return one;
            case 2: case 3: case 4: return few;
            default: return many;
        }
    }

    /** Есть ли вообще что применять. Пустой список в любом режиме — не правило. */
    boolean isEmpty() {
        return packages().isEmpty();
    }

    /**
     * Краткое описание для экрана настроек.
     *
     * <p>Говорим про ПОСЛЕДСТВИЕ, а не про механику: человеку важно, что часть
     * приложений пойдёт открытым, а не то, каким системным вызовом это сделано.
     */
    String summary() {
        int n = packages().size();
        if (n == 0) return "Все приложения идут через туннель.";
        String apps = n + " " + plural(n, "приложение", "приложения", "приложений");
        if (MODE_ONLY.equals(mode())) {
            return "Через туннель идёт только " + apps
                    + ". Остальные — открытым, со своим адресом.";
        }
        return "Мимо туннеля идёт " + apps
                + " — открытым, со своим адресом. Остальные защищены.";
    }
}
