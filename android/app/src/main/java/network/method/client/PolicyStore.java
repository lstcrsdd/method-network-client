package network.method.client;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Хранение пользовательских сценариев.
 *
 * <p>Сценарий — это политика полос: какой трафик каким ПУТЁМ идёт. Правило
 * называет полосу, а не сервер, поэтому его не приходится переписывать при
 * смене узлов, а смена маршрута не трогает правила.
 *
 * <p>Формат — тот же переносимый, что на macOS и в общем слое. Один формат на
 * все платформы означает, что политику можно будет переносить между
 * устройствами, не изобретая обмена.
 *
 * <p>Шифруется вместе с остальным: правила описывают, какие сайты человек
 * выводит мимо туннеля, — то есть и список сайтов, и сам факт интереса к ним.
 */
final class PolicyStore {

    private static final String TAG = "MethodPolicy";
    private static final String FILE = "method-policy";
    private static final String KEY = "policy";

    private final SharedPreferences prefs;
    private final Store store;

    PolicyStore(Context context) {
        prefs = context.getSharedPreferences(FILE, Context.MODE_PRIVATE);
        // Проверка политики зависит от того, какие оси вообще есть у человека:
        // «полосе нужно два независимых способа» бессмысленно проверять, не
        // зная списка узлов.
        store = new Store(context);
    }

    /** Политика в переносимом формате. Пусто — значит заводская. */
    String json() {
        String raw = prefs.getString(KEY, "");
        if (raw.isEmpty()) return "";
        try {
            return Secrets.open(raw);
        } catch (Throwable t) {
            Log.e(TAG, "политика не расшифровывается, берём заводскую: " + t);
            return "";
        }
    }

    void save(JSONObject policy) {
        try {
            prefs.edit().putString(KEY, Secrets.seal(policy.toString())).apply();
        } catch (Throwable t) {
            Log.e(TAG, "политика не записана: " + t);
        }
    }

    void reset() {
        prefs.edit().remove(KEY).apply();
    }

    /**
     * Загрузить как объект. При пустой политике собирает заводскую здесь же,
     * чтобы экрану было что показать до первой правки.
     *
     * <p>Заводская повторяет ту, что в общем слое: весь трафик через туннель,
     * локальная сеть мимо. Дублирование сознательное и маленькое — экран обязан
     * что-то нарисовать ещё до того, как ядро хоть раз поднималось.
     */
    JSONObject load() throws Exception {
        String raw = json();
        if (!raw.isEmpty()) return new JSONObject(raw);

        JSONObject web = new JSONObject()
                .put("id", "web").put("title", "Весь трафик")
                .put("allowsDirect", false).put("minAxes", 2);
        JSONObject lan = new JSONObject()
                .put("id", "lan").put("title", "Локальная сеть")
                .put("allowsDirect", true)
                .put("justification", "Принтеры, NAS и шлюз в домашней сети")
                .put("minAxes", 1);
        JSONObject privateRule = new JSONObject()
                .put("kind", "private").put("values", new JSONArray())
                .put("lane", "lan");
        return new JSONObject()
                .put("version", 1)
                .put("lanes", new JSONArray().put(web).put(lan))
                .put("flows", new JSONArray().put(privateRule))
                .put("defaultLane", "web");
    }

    /**
     * Добавить правило «эти сайты — такой-то полосой».
     *
     * @return сообщение об отказе либо null, если принято
     */
    String addDomainRule(String[] domains, String laneId, String justification) {
        try {
            JSONObject policy = load();
            JSONArray lanes = policy.getJSONArray("lanes");

            // Объяснение относится к ПОЛОСЕ, а не к правилу: открытым трафик
            // выпускает полоса, значит она и обязана нести причину.
            for (int i = 0; i < lanes.length(); i++) {
                JSONObject lane = lanes.getJSONObject(i);
                if (lane.getString("id").equals(laneId) && lane.optBoolean("allowsDirect")) {
                    String text = justification == null ? "" : justification.trim();
                    if (!text.isEmpty()) lane.put("justification", text);
                }
            }

            JSONArray values = new JSONArray();
            for (String d : domains) values.put(d);
            policy.getJSONArray("flows").put(new JSONObject()
                    .put("kind", "domainSuffix")
                    .put("values", values)
                    .put("lane", laneId));

            // Проверяем ОБЩИМ СЛОЕМ, а не своими силами: правило, до которого
            // очередь не дойдёт, и негодная политика ловятся там же, где на
            // macOS и Windows.
            JSONObject report = new JSONObject(
                    MethodCore.policyReport(policy.toString(), store.profilesJson()));
            if (!report.optBoolean("valid", true)) {
                return report.optString("error");
            }
            JSONArray shadowed = report.optJSONArray("shadowed");
            int last = policy.getJSONArray("flows").length() - 1;
            if (shadowed != null) {
                for (int i = 0; i < shadowed.length(); i++) {
                    if (shadowed.getJSONObject(i).optInt("index") == last) {
                        return "Это правило не сработает: выше уже стоит правило на те же сайты."
                                + " Удалите его — иначе ядро возьмёт первое.";
                    }
                }
            }
            save(policy);
            return null;
        } catch (Throwable t) {
            return String.valueOf(t.getMessage());
        }
    }
}
