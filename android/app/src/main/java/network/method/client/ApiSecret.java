package network.method.client;

import android.content.Context;
import android.content.SharedPreferences;

import java.security.SecureRandom;

/**
 * Секрет локального API ядра — случайный на установку.
 *
 * <p>Раньше во всех клиентах стояла вкомпилированная строка, одинаковая у
 * каждой установки. Секрет даёт право переключать полосы и читать список
 * соединений — то есть видеть, КУДА ходит человек. Вкомпилированное значение
 * означало, что это право есть у любого приложения, которое прочитало наш
 * открытый код.
 *
 * <p>Чего секрет НЕ даёт, и это заложено в схеме полос: вывести трафик за
 * пределы объявленного набора нельзя даже с ним — ядро отвечает отказом на
 * увод полосы за пределы её селектора.
 */
final class ApiSecret {

    private static final String FILE = "method-api";
    private static final String KEY = "clash-secret";

    private ApiSecret() {}

    static String get(Context context) {
        SharedPreferences prefs = context.getSharedPreferences(FILE, Context.MODE_PRIVATE);
        String stored = prefs.getString(KEY, null);
        if (stored != null && !stored.isEmpty()) {
            try {
                // Секрет шифруется тем же ключом, что профили: он даёт право
                // видеть список соединений, то есть КУДА ходит человек.
                return Secrets.open(stored);
            } catch (Throwable ignored) {
                // Не расшифровался — считаем, что секрета нет, и заводим новый.
                // Ядро всё равно поднимется со свежим: он записывается в конфиг
                // при каждом подключении.
            }
        }

        byte[] bytes = new byte[32];
        new SecureRandom().nextBytes(bytes);
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) sb.append(String.format("%02x", b));
        String fresh = sb.toString();
        try {
            prefs.edit().putString(KEY, Secrets.seal(fresh)).apply();
        } catch (Throwable ignored) {
            // Не сохранился — не беда: при следующем подключении заведётся
            // новый. Записывать открытым «на всякий случай» нельзя.
        }
        return fresh;
    }
}
