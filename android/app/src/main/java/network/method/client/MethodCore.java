package network.method.client;

import android.util.Log;

/**
 * Общий слой Method: разбор share-ссылок и сборка конфига ядра.
 *
 * <p>Код живёт на Rust и один на все клиенты — macOS, Windows, Android. До
 * этого модель, парсер и сборщик были написаны дважды, и расхождение началось
 * сразу: у Windows нет Trojan и Shadowsocks, две оси обхода из пяти
 * отсутствуют не по замыслу, а потому что код не общий.
 *
 * <p>Через границу ходит JSON, а не плоские структуры. Размен осознанный:
 * конфиг собирается один раз на подключение, а плоский интерфейс на каждое
 * поле профиля ломался бы при добавлении любой настройки протокола.
 *
 * <p>Отказ приходит как {@link IllegalArgumentException} с человеческим
 * объяснением: негодная ссылка, неразбираемая политика, полоса остатка с
 * прямым выходом. Отказ здесь дешевле аварии у пользователя.
 */
public final class MethodCore {

    private static final String TAG = "MethodCore";
    private static boolean loaded;

    static {
        boolean ok = false;
        try {
            System.loadLibrary("method_android");
            ok = true;
        } catch (UnsatisfiedLinkError e) {
            // Не роняем приложение: старый путь со вшитым каталогом работает и
            // без общего слоя. Но молчать нельзя — иначе разница «нет
            // библиотеки» и «библиотека врёт» неотличима в отчёте.
            Log.e(TAG, "не загрузилась libmethod_android: " + e.getMessage());
        }
        loaded = ok;
    }

    private MethodCore() {}

    /** Доступен ли общий слой. */
    public static boolean isAvailable() {
        return loaded;
    }

    /** Разобрать одну ссылку. Наружу — профиль в JSON. */
    public static native String parseLink(String link);

    /**
     * Разобрать тело подписки построчно.
     *
     * <p>Наружу: {@code {"profiles":[...],"errors":[{"line":N,"message":"..."}]}}.
     * Кривая строка НЕ роняет остальные: подписка на два десятка узлов,
     * потерянная из-за одной записи, — худший вид отказа.
     */
    public static native String parseMany(String text);

    /**
     * Собрать конфиг ядра в схеме полос.
     *
     * @param profilesJson массив профилей
     * @param policyJson   политика в переносимом формате; null или пусто —
     *                     заводская
     * @param optionsJson  настройки сборки
     */
    public static native String buildLanes(String profilesJson, String policyJson, String optionsJson);

    /**
     * Разбор политики: годна ли и какие правила перекрыты.
     *
     * <p>Обе проверки живут в общем слое. Перекрытое правило — то, до которого
     * очередь не дойдёт никогда: ядро берёт первое совпавшее сверху вниз, а
     * человек видит в списке строку и считает, что она действует.
     *
     * @return {@code {"valid":bool,"error":"…","shadowed":[{"index":N,"by":M}]}}
     */
    public static native String policyReport(String policyJson, String profilesJson);

    /**
     * Опись узлов для экрана серверов, сгруппированная по осям обхода.
     *
     * <p>Какая у профиля ось и как она называется — знает общий слой. Выводить
     * это на Java из протокола и транспорта значило бы завести второе мнение,
     * которое разойдётся с остальными платформами на первой же новинке.
     *
     * @return {@code {"axes":[{"key","human","nodes":[…]}],"total":N}}
     */
    public static native String inventory(String profilesJson);

    /**
     * Самопроверка моста на живом устройстве.
     *
     * <p>Нужна потому, что JNI ломается тихо: библиотека не той архитектуры,
     * не совпавшая сигнатура, потерянное исключение — всё это выглядит как
     * «приложение просто закрылось». Прогон при запуске превращает такой отказ
     * в строку журнала, а не в загадку.
     *
     * @return краткий итог для журнала
     */
    public static String selfTest() {
        if (!loaded) {
            return "мост недоступен: библиотека не загрузилась";
        }
        try {
            String profile = parseLink(
                    "hysteria2://shared:pw@1.2.3.4:443/?sni=www.microsoft.com&insecure=1#US");
            if (!profile.contains("hysteria2")) {
                return "разбор ссылки дал неожиданное: " + profile;
            }
            String many = parseMany("hysteria2://pw@1.2.3.4:443#A\nмусор");
            if (!many.contains("\"line\":2")) {
                return "номер кривой строки не назван: " + many;
            }
            String profiles = "[{\"name\":\"A\",\"protocol\":\"hysteria2\",\"host\":\"1.2.3.4\","
                    + "\"port\":443,\"parameters\":{\"kind\":\"hysteria2\",\"hysteria2\":"
                    + "{\"password\":\"p\",\"alpn\":[\"h3\"]}}},"
                    + "{\"name\":\"B\",\"protocol\":\"trojan\",\"host\":\"a.example.com\","
                    + "\"port\":8443,\"parameters\":{\"kind\":\"trojan\",\"trojan\":"
                    + "{\"password\":\"p\"}}}]";
            String plan = buildLanes(profiles, null, "{\"clashSecret\":\"s\"}");
            if (!plan.contains("OUT_block") || plan.contains("\"L.web\":[\"OUT_direct")) {
                return "конфиг полос собран неверно";
            }
            // И отказ обязан быть отказом, а не молчаливой сборкой.
            try {
                buildLanes(profiles,
                        "{\"version\":1,\"lanes\":[{\"id\":\"web\",\"title\":\"В\","
                                + "\"allowsDirect\":true,\"justification\":\"з\",\"minAxes\":1}],"
                                + "\"flows\":[],\"defaultLane\":\"web\"}",
                        "{\"clashSecret\":\"s\"}");
                return "негодная политика СОБРАЛАСЬ — это дыра";
            } catch (IllegalArgumentException expected) {
                // так и надо
            }
            return "мост исправен: разбор, подписка, полосы, отказ негодной политики";
        } catch (Throwable t) {
            return "мост упал: " + t;
        }
    }
}
