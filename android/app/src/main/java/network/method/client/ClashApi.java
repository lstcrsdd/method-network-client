package network.method.client;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;

/**
 * Управление живым ядром: локальный HTTP-интерфейс sing-box.
 *
 * <p>Через него делается ровно три вещи, и больше ядро ничего не позволяет
 * менять на ходу: узнать состав и текущий выбор групп, переключить выбор
 * внутри группы, оборвать живые соединения. Пересобрать конфиг нельзя —
 * `PUT /configs` отвечает согласием и не делает ничего.
 *
 * <p><b>Почему увести трафик мимо туннеля через этот интерфейс нельзя.</b>
 * Смена выбора работает только в пределах состава селектора: попытка назначить
 * полосе то, чего в её составе нет, отвергается самим ядром. Полоса «весь
 * трафик» содержит оси и блокировку — прямого выхода там нет, значит нет и
 * состояния, в котором трафик пошёл бы открытым.
 */
final class ClashApi {

    private final String base;
    private final String secret;

    ClashApi(String controller, String secret) {
        this.base = "http://" + controller;
        this.secret = secret;
    }

    /** Состав групп и текущий выбор. */
    JSONObject proxies() throws Exception {
        return new JSONObject(request("GET", "/proxies", null));
    }

    /**
     * Переключить полосу на другую ось.
     *
     * @return true, если ядро приняло; false — если отвергло (обычно это
     *         попытка увести за пределы состава, то есть защита сработала)
     */
    boolean select(String lane, String member) throws Exception {
        JSONObject body = new JSONObject().put("name", member);
        try {
            request("PUT", "/proxies/" + enc(lane), body.toString());
            return true;
        } catch (HttpFailure f) {
            return false;
        }
    }

    /**
     * Замер задержки через группу.
     *
     * <p>Адрес обязан быть https. Ядро игнорирует http и молча подставляет
     * собственный адрес на gstatic — измерялся бы чужой сервер, а не наш путь.
     *
     * @return задержка в миллисекундах либо -1, если ось не ответила
     */
    int delay(String member, String url, int timeoutMs) {
        try {
            String path = "/proxies/" + enc(member) + "/delay?timeout=" + timeoutMs
                    + "&url=" + enc(url);
            JSONObject r = new JSONObject(request("GET", path, null));
            return r.optInt("delay", -1);
        } catch (Throwable t) {
            return -1;
        }
    }

    /**
     * Оборвать живые соединения полосы.
     *
     * <p>Зовётся только при аварии: при плановом переключении сессии дотекают
     * по прежнему пути, и рвать их — значит портить человеку загрузку ради
     * красоты. А вот на мёртвом пути они не текут никуда, и держать их — значит
     * держать человека без интернета.
     */
    void closeConnections() {
        try {
            request("DELETE", "/connections", null);
        } catch (Throwable ignored) {
        }
    }

    // ───────────────────────────── внутреннее ─────────────────────────────

    private String request(String method, String path, String body) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(base + path).openConnection();
        try {
            c.setRequestMethod(method);
            c.setConnectTimeout(3000);
            c.setReadTimeout(15000);
            c.setRequestProperty("Authorization", "Bearer " + secret);
            if (body != null) {
                c.setDoOutput(true);
                c.setRequestProperty("Content-Type", "application/json");
                try (OutputStream out = c.getOutputStream()) {
                    out.write(body.getBytes(StandardCharsets.UTF_8));
                }
            }
            int code = c.getResponseCode();
            if (code < 200 || code >= 300) {
                throw new HttpFailure(code, read(c.getErrorStream()));
            }
            return read(c.getInputStream());
        } finally {
            c.disconnect();
        }
    }

    private static String read(InputStream in) throws Exception {
        if (in == null) return "";
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[4096];
        int n;
        while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
        return new String(out.toByteArray(), StandardCharsets.UTF_8);
    }

    private static String enc(String s) throws Exception {
        return URLEncoder.encode(s, "UTF-8").replace("+", "%20");
    }

    static final class HttpFailure extends Exception {
        final int code;

        HttpFailure(int code, String body) {
            super("ядро ответило " + code + ": " + body);
            this.code = code;
        }
    }
}
