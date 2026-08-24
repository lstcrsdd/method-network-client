package network.method.client;

import android.util.Base64;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/**
 * Забор подписки.
 *
 * <p>Тело обычно приходит в base64 — сплошной строкой без переводов строк.
 * Раскодировать надо ДО разбора: общий слой ждёт ссылки, а не основание из
 * букв. Но встречается и открытый список, поэтому решаем по содержимому, а не
 * по вере в формат.
 */
final class Subscriptions {

    private Subscriptions() {}

    static String fetch(String url) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        try {
            c.setConnectTimeout(15000);
            c.setReadTimeout(20000);
            c.setInstanceFollowRedirects(true);
            // Панели узнают клиента по этому полю и иногда отдают разный формат.
            c.setRequestProperty("User-Agent", "Method/1.0 (Android)");
            int code = c.getResponseCode();
            if (code != 200) {
                throw new IllegalStateException("подписка ответила " + code);
            }
            String body = read(c.getInputStream());
            return decodeIfBase64(body);
        } finally {
            c.disconnect();
        }
    }

    private static String read(InputStream in) throws Exception {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
        return new String(out.toByteArray(), StandardCharsets.UTF_8);
    }

    /**
     * Уже ссылки — отдаём как есть. Иначе пробуем base64 и проверяем результат:
     * если после раскодирования ссылок не появилось, значит это был не base64,
     * и подсовывать ядру мусор незачем.
     */
    private static String decodeIfBase64(String body) {
        String trimmed = body.trim();
        if (looksLikeLinks(trimmed)) return trimmed;
        try {
            byte[] raw = Base64.decode(trimmed, Base64.DEFAULT);
            String decoded = new String(raw, StandardCharsets.UTF_8);
            if (looksLikeLinks(decoded)) return decoded;
        } catch (Throwable ignored) {
        }
        return trimmed;
    }

    private static boolean looksLikeLinks(String text) {
        return text.contains("hysteria2://") || text.contains("hy2://")
                || text.contains("vless://") || text.contains("trojan://")
                || text.contains("ss://");
    }
}
