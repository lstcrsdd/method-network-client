package network.method.client;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/**
 * Хранилище своих конфигураций: ссылки и подписки.
 *
 * <p>Главное отличие нового клиента от старого — здесь НЕТ вшитого каталога.
 * В старом серверы лежат в {@code assets/servers.json}, а значит пароли есть у
 * каждого, кто распаковал APK. Тут приложение приезжает пустым, и всё, что в
 * нём появляется, кладёт сам человек.
 *
 * <p>Профили хранятся строкой JSON — ровно в том виде, в каком их отдаёт общий
 * слой на Rust. Разбирать их в Java незачем: разбор один на все платформы, и
 * второй реализации быть не должно.
 *
 * <p>Лежат профили ЗАШИФРОВАННЫМИ — ключом из Keystore, см. {@link Secrets}.
 * Открытым текстом они попадали бы в отладочные выгрузки и были бы доступны
 * любому, у кого есть root. Записанное прежними сборками открытым текстом
 * читается и молча перекладывается на шифрованное при первом же доступе.
 */
final class Store {

    private static final String TAG = "MethodStore";
    private static final String FILE = "method-store";
    private static final String KEY_PROFILES = "profiles";
    private static final String KEY_SUBSCRIPTIONS = "subscriptions";
    /** Источники: подписки и ручной список. См. {@link #sources()}. */
    private static final String KEY_SOURCES = "sources";
    private static final String KEY_SELECTED = "selected";

    private final SharedPreferences prefs;

    Store(Context context) {
        prefs = context.getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    /** Профили в том виде, в каком их понимает общий слой: массив JSON. */
    String profilesJson() {
        String raw = prefs.getString(KEY_PROFILES, "[]");
        if (!Secrets.isSealed(raw)) {
            // Написано прежней сборкой открытым текстом. Перекладываем на
            // шифрованное сразу: отложенный перенос означает, что открытая
            // копия живёт на диске до следующей правки списка, то есть,
            // возможно, вечно.
            if (!"[]".equals(raw)) {
                write(raw);
                Log.i(TAG, "профили переложены в зашифрованное хранилище");
            }
            return raw;
        }
        try {
            return Secrets.open(raw);
        } catch (Throwable t) {
            // Ключ потерян (переустановка, сброс Keystore). Восстановить нечем:
            // молча отдать пустой список честнее, чем притворяться, что
            // серверов не было.
            Log.e(TAG, "профили не расшифровываются: " + t);
            return "[]";
        }
    }

    /** Записать список профилей, зашифровав. */
    private void write(String json) {
        try {
            prefs.edit().putString(KEY_PROFILES, Secrets.seal(json)).apply();
        } catch (Throwable t) {
            // Шифрование недоступно — не пишем ВОВСЕ. Записать открытым в
            // качестве «запасного варианта» значило бы тихо понизить защиту
            // ровно там, где человек считает её включённой.
            Log.e(TAG, "не записано: шифрование недоступно: " + t);
        }
    }

    int profileCount() {
        try {
            return new JSONArray(profilesJson()).length();
        } catch (Exception e) {
            return 0;
        }
    }

    /**
     * Добавить профили из текста: одна ссылка или целое тело подписки.
     *
     * @return сколько добавлено и сколько строк оказалось негодными
     */
    /**
     * Источники узлов.
     *
     * <p>Подписка — не мешок ссылок, а ЖИВОЙ список, за который отвечает тот,
     * кто его выдаёт. Раньше узлы всех источников сваливались в один плоский
     * массив, и обновление подписки только ДОБАВЛЯЛО: узел, который провайдер
     * убрал — сломанный, просроченный, выключенный, — оставался у человека
     * навсегда и продолжал участвовать в выборе пути. Разъехаться с
     * действительностью такой список мог за неделю.
     *
     * <p>Теперь у каждого источника свой набор, и обновление ЗАМЕНЯЕТ его
     * целиком. Ручные ссылки живут отдельным источником: их никто не
     * обновляет, и стирать их вместе с подпиской было бы враньём.
     *
     * @return массив {@code [{"id","title","url","updatedAt","profiles":[…]}]}
     */
    JSONArray sources() {
        try {
            String raw = prefs.getString(KEY_SOURCES, "");
            if (!raw.isEmpty()) return new JSONArray(Secrets.open(raw));
        } catch (Throwable t) {
            Log.e(TAG, "источники не читаются: " + t);
        }
        // Переезд со старого плоского списка: всё, что было, становится
        // ручным источником. Терять узлы человека при обновлении клиента
        // недопустимо.
        JSONArray out = new JSONArray();
        try {
            JSONArray flat = new JSONArray(profilesJson());
            if (flat.length() > 0) {
                // Чей это список, мы точно не знаем — источников до сих пор не
                // было. Но если подписка сохранена, узлы почти наверняка
                // пришли ИЗ НЕЁ, и приписать их подписке важно: иначе первое
                // же обновление добавит те же узлы второй группой, и человек
                // получит каталог вдвое больше настоящего.
                List<String> subs = subscriptions();
                boolean fromSub = !subs.isEmpty();
                String id = fromSub ? subs.get(0) : "manual";
                out.put(new JSONObject()
                        .put("id", id)
                        .put("title", fromSub ? titleOf(id) : "Свои ссылки")
                        .put("url", fromSub ? id : "")
                        .put("updatedAt", 0)
                        .put("profiles", flat));
            }
        } catch (Throwable ignored) {
        }
        return out;
    }

    private void writeSources(JSONArray sources) {
        try {
            prefs.edit().putString(KEY_SOURCES, Secrets.seal(sources.toString())).apply();
        } catch (Throwable t) {
            Log.e(TAG, "источники не записаны: " + t);
        }
        // Плоский список остаётся как есть: его читает сборщик конфига, и
        // держать два представления в согласии дешевле, чем переучивать всё
        // разом.
        JSONArray all = new JSONArray();
        for (int i = 0; i < sources.length(); i++) {
            JSONArray ps = sources.optJSONObject(i) == null
                    ? null : sources.optJSONObject(i).optJSONArray("profiles");
            for (int j = 0; ps != null && j < ps.length(); j++) all.put(ps.opt(j));
        }
        write(all.toString());
    }

    /** Удалить источник целиком вместе с его узлами. */
    void removeSource(String id) {
        JSONArray src = sources();
        JSONArray kept = new JSONArray();
        for (int i = 0; i < src.length(); i++) {
            JSONObject o = src.optJSONObject(i);
            if (o != null && !id.equals(o.optString("id"))) kept.put(o);
        }
        writeSources(kept);
        if (!"manual".equals(id)) forgetSubscription(id);
    }

    private void forgetSubscription(String url) {
        List<String> list = subscriptions();
        if (!list.remove(url)) return;
        JSONArray a = new JSONArray();
        for (String u : list) a.put(u);
        prefs.edit().putString(KEY_SUBSCRIPTIONS, a.toString()).apply();
    }

    /**
     * Добавить узлы в источник.
     *
     * @param sourceUrl адрес подписки либо null для ручных ссылок
     */
    Result add(String text, String sourceUrl) {
        if (!MethodCore.isAvailable()) {
            return Result.failure("общий слой недоступен: библиотека не загрузилась");
        }
        try {
            JSONObject parsed = new JSONObject(MethodCore.parseMany(text));
            JSONArray fresh = parsed.getJSONArray("profiles");
            JSONArray errors = parsed.optJSONArray("errors");
            int bad = errors == null ? 0 : errors.length();
            if (fresh.length() == 0) {
                return Result.failure(bad > 0
                        ? "ни одной годной ссылки (негодных строк: " + bad + ")"
                        : "ни одной ссылки не найдено");
            }

            String id = sourceUrl == null || sourceUrl.isEmpty() ? "manual" : sourceUrl;
            JSONArray src = sources();
            JSONArray out = new JSONArray();
            boolean replaced = false;

            for (int i = 0; i < src.length(); i++) {
                JSONObject o = src.getJSONObject(i);
                if (!id.equals(o.optString("id"))) { out.put(o); continue; }
                replaced = true;
                if ("manual".equals(id)) {
                    // Ручные накапливаем: человек добавляет ссылки по одной, и
                    // замена стёрла бы предыдущие.
                    JSONArray have = o.optJSONArray("profiles");
                    if (have == null) have = new JSONArray();
                    for (int j = 0; j < fresh.length(); j++) {
                        if (!contains(have, fresh.getJSONObject(j))) have.put(fresh.get(j));
                    }
                    o.put("profiles", have);
                } else {
                    // Подписку ЗАМЕНЯЕМ целиком — в этом весь смысл подписки.
                    o.put("profiles", fresh);
                }
                o.put("updatedAt", System.currentTimeMillis());
                out.put(o);
            }

            if (!replaced) {
                out.put(new JSONObject()
                        .put("id", id)
                        .put("title", "manual".equals(id) ? "Свои ссылки" : titleOf(sourceUrl))
                        .put("url", "manual".equals(id) ? "" : sourceUrl)
                        .put("updatedAt", System.currentTimeMillis())
                        .put("profiles", fresh));
            }

            writeSources(out);
            Log.i(TAG, "источник " + id + ": узлов " + fresh.length() + ", негодных строк " + bad);
            return Result.success(fresh.length(), bad);
        } catch (Throwable t) {
            return Result.failure(String.valueOf(t.getMessage()));
        }
    }

    /** Короткое имя подписки — узел адреса. Полный URL в списке не место. */
    private static String titleOf(String url) {
        try {
            String host = new java.net.URL(url).getHost();
            return host == null || host.isEmpty() ? "Подписка" : host;
        } catch (Throwable t) {
            return "Подписка";
        }
    }

    /** Ручные ссылки. */
    Result add(String text) {
        return add(text, null);
    }

    /**
     * Узел считается тем же, если совпали адрес, порт и протокол. Имя в расчёт
     * не берётся: провайдеры переименовывают узлы между обновлениями подписки,
     * и сравнение по имени плодило бы дубликаты на ровном месте.
     */
    private boolean contains(JSONArray all, JSONObject candidate) {
        for (int i = 0; i < all.length(); i++) {
            JSONObject p = all.optJSONObject(i);
            if (p == null) continue;
            if (p.optString("host").equals(candidate.optString("host"))
                    && p.optInt("port") == candidate.optInt("port")
                    && p.optString("protocol").equals(candidate.optString("protocol"))) {
                return true;
            }
        }
        return false;
    }

    void clear() {
        prefs.edit().remove(KEY_SOURCES).apply();
        prefs.edit().remove(KEY_PROFILES).remove(KEY_SELECTED).apply();
    }

    List<String> subscriptions() {
        List<String> out = new ArrayList<>();
        try {
            JSONArray a = new JSONArray(prefs.getString(KEY_SUBSCRIPTIONS, "[]"));
            for (int i = 0; i < a.length(); i++) out.add(a.getString(i));
        } catch (Exception ignored) {
        }
        return out;
    }

    void rememberSubscription(String url) {
        List<String> list = subscriptions();
        if (list.contains(url)) return;
        list.add(url);
        prefs.edit().putString(KEY_SUBSCRIPTIONS, new JSONArray(list).toString()).apply();
    }

    /** Итог добавления: сколько приехало и что пошло не так. */
    static final class Result {
        final boolean ok;
        final int added;
        final int rejected;
        final String message;

        private Result(boolean ok, int added, int rejected, String message) {
            this.ok = ok;
            this.added = added;
            this.rejected = rejected;
            this.message = message;
        }

        static Result success(int added, int rejected) {
            return new Result(true, added, rejected, null);
        }

        static Result failure(String message) {
            return new Result(false, 0, 0, message);
        }
    }
}
