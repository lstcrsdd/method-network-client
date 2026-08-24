package network.method.client;

import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * Управляющий контур: измеряет оси и меняет привязку полос на ЖИВОМ ядре.
 *
 * <p>Работает поверх уже поднятого туннеля и никогда его не пересоздаёт. Это
 * главное отличие от «переключения сервера» в старых клиентах: там смена узла
 * означала остановку ядра и подъём заново — обрыв всех соединений и провал
 * интернета на несколько секунд. Здесь смена пути — один запрос к локальному
 * интерфейсу, и человек её не замечает.
 *
 * <p>Контур сознательно НЕ умеет переподключаться и поднимать туннель. Этим
 * занимается служба со своим сторожем, и два механизма, независимо решающих
 * «переподключиться», дали бы ровно тот дребезг, от которого мы защищаемся.
 */
final class LaneOrchestrator {

    private static final String TAG = "MethodLanes";

    /**
     * Шаг цикла. Значение обязано совпадать с объявленным движку: он считает от
     * него возраст замеров. На macOS расхождение (реальные 13 секунд при
     * объявленных 6) держало движок вечно непрогретым — он честно отвечал
     * «кандидатов нет» и не решал ничего.
     */
    private static final long STEP_MS = 6000;

    /**
     * Шаг прогрева. Порог участия в выборе движок берёт примерно на пятидесятой
     * пробе; при шаге в шесть секунд это пять минут молчания. При двух —
     * меньше двух минут.
     */
    private static final long WARMUP_STEP_MS = 2000;

    /**
     * Тайм-аут пробы. 2.5 секунды — с запасом больше всего, что мы наблюдали:
     * рабочие задержки 70–420 мс, самое долгое рукопожатие 1.8 с (замер по 117
     * кругам, см. брифинг). Больше ставить нельзя: молчащая ось есть почти
     * всегда — на российских каналах это поток без рукопожатия, — и её
     * тайм-аут задаёт нижнюю границу длины круга для ВСЕХ остальных.
     */
    private static final int PROBE_TIMEOUT_MS = 2500;

    /** Тег полосы остатка. Тот же, что строит общий слой. */
    private static final String DEFAULT_LANE = "L.web";

    /** Адрес проб. Только https — ядро игнорирует http и подставляет своё. */
    private static final String PROBE_URL = "https://www.gstatic.com/generate_204";

    private final android.content.Context context;
    private final ClashApi api;
    private final Listener listener;
    private final ScheduledExecutorService timer = Executors.newSingleThreadScheduledExecutor();
    /**
     * Пул для проб. Осей пять и больше не станет: способов пройти столько,
     * сколько независимых форм трафика мы умеем, а не сколько узлов куплено.
     */
    private final java.util.concurrent.ExecutorService probePool =
            Executors.newFixedThreadPool(6);

    private MethodEngine engine;
    /** Тег оси в конфиге ядра → дескриптор в движке, и обратно. */
    private final Map<String, Integer> axisByTag = new HashMap<>();
    private final Map<Integer, String> tagByAxis = new HashMap<>();
    private final Map<String, Integer> laneByTag = new HashMap<>();
    private final Map<Integer, String> tagByLane = new HashMap<>();
    /** Полосы, которым уже назначали ось: отличает «нет живых» от «ещё не мерил». */
    private final Set<String> everApplied = new HashSet<>();
    /** Полосы, которым уже объявляли решение: первый выбор человеку не сообщается. */
    private final Set<String> announced = new HashSet<>();

    private volatile boolean running;
    private long ticks;
    private boolean decided;
    /** Сколько кругов подряд молчит действующий путь. См. rescueFromDeadAxis. */
    private int deadStreak;

    interface Listener {
        /** Что сделала автоматика и почему. Без объяснения она читается как своеволие. */
        void onDecision(String humanText);

        /**
         * Состояние путей после круга измерений.
         *
         * @param pathHuman человеческое имя действующей оси
         * @param alive     сколько способов пройти ответило
         * @param total     сколько объявлено
         */
        void onPaths(String pathHuman, int alive, int total);

        /**
         * Тег маршрута, который несёт трафик прямо сейчас.
         *
         * <p>Отдаём именно ТЕГ, а не готовую подпись: внутри оси маршрут
         * выбирает само ядро, и мы узнаём его выбор здесь, а как назвать узел
         * человеку — знает служба, которая собирала конфиг.
         */
        void onNode(String routeTag);
    }

    /** Человеческие имена осей — те же, что в общем слое и на macOS. */
    private static String humanAxis(String key) {
        switch (key) {
            case "quic-udp":     return "QUIC поверх UDP";
            case "fake-tls-h2":  return "gRPC в поддельном TLS";
            case "fake-tls-tcp": return "TCP в поддельном TLS";
            case "real-tls":     return "настоящий TLS";
            case "raw-stream":   return "поток без рукопожатия";
            default:             return key;
        }
    }

    LaneOrchestrator(android.content.Context context, ClashApi api, Listener listener) {
        this.context = context.getApplicationContext();
        this.api = api;
        this.listener = listener;
    }

    /**
     * Память движка между подключениями.
     *
     * <p>Порог участия набирается примерно за две минуты замеров. Без переноса
     * состояния КАЖДОЕ переподключение — смена сети, выход из сна, правка
     * сценариев — начинает счёт заново, и всё это время выбор пути остаётся за
     * ядром, которое сравнивать оси не умеет. Проверено на живом телефоне
     * 2026-08-24: после переподключения контур честно отсчитал 49 кругов
     * заново.
     *
     * <p>Возраст замеров передаётся отдельно: движок обязан знать, что данные
     * получасовой давности описывают другую сеть, а не сегодняшнюю.
     */
    /**
     * Предельная давность памяти движка.
     *
     * <p>Двадцать минут — по смыслу задачи, а не по вкусу. Переподключение,
     * выход из сна, смена сети, правка сценариев укладываются в минуты, и там
     * память экономит две минуты слепоты. Всё, что дольше, — это уже другая
     * обстановка: человек уехал, сменил сеть, время суток поменяло загрузку
     * канала.
     */
    private static final long STATE_MAX_AGE_MS = 20 * 60 * 1000L;

    private static final String STATE_FILE = "method-engine-state";
    private static final String STATE_KEY = "state";
    private static final String STATE_AT = "at";

    private void restoreState(long nowMs) {
        try {
            android.content.SharedPreferences prefs =
                    context.getSharedPreferences(STATE_FILE, android.content.Context.MODE_PRIVATE);
            String sealed = prefs.getString(STATE_KEY, "");
            long savedAt = prefs.getLong(STATE_AT, 0);
            if (sealed.isEmpty() || savedAt <= 0) return;
            long elapsed = Math.max(0, System.currentTimeMillis() - savedAt);
            if (elapsed > STATE_MAX_AGE_MS) {
                // Протухло. Замеры трёхчасовой давности описывают другую сеть:
                // другой Wi-Fi, другой город, другую загрузку канала. Тащить их
                // в новое подключение не просто бесполезно — вредно.
                //
                // Поймано на живом телефоне 2026-08-24: движок поднял состояние
                // давностью 10929 секунд и не смог набрать порог участия за
                // СЕМЬ минут, тогда как с чистого листа решал за две. Свежие
                // замеры тонули в протухших.
                Log.i(TAG, "состояние протухло (" + (elapsed / 60000) + " мин), начинаем заново");
                return;
            }
            byte[] data = android.util.Base64.decode(Secrets.open(sealed), android.util.Base64.NO_WRAP);
            engine.loadState(data, nowMs, elapsed);
            Log.i(TAG, "состояние движка восстановлено, давность " + (elapsed / 1000) + " с");
        } catch (Throwable t) {
            // Негодное состояние — не беда: движок просто начнёт с чистого
            // листа. Ронять из-за этого контур нельзя.
            Log.i(TAG, "состояние не восстановлено, начинаем заново: " + t);
        }
    }

    private void persistState() {
        if (engine == null) return;
        try {
            byte[] data = engine.saveState(System.nanoTime() / 1_000_000L);
            if (data == null || data.length == 0) return;
            String text = android.util.Base64.encodeToString(data, android.util.Base64.NO_WRAP);
            context.getSharedPreferences(STATE_FILE, android.content.Context.MODE_PRIVATE)
                    .edit()
                    .putString(STATE_KEY, Secrets.seal(text))
                    .putLong(STATE_AT, System.currentTimeMillis())
                    .apply();
        } catch (Throwable t) {
            Log.i(TAG, "состояние не сохранено: " + t);
        }
    }

    void start() {
        if (running) return;
        running = true;
        schedule(0);
    }

    void stop() {
        running = false;
        timer.shutdownNow();
        probePool.shutdownNow();
        persistState();
        if (engine != null) {
            engine.close();
            engine = null;
        }
    }

    private void schedule(long delayMs) {
        if (!running) return;
        try {
            timer.schedule(this::tick, delayMs, TimeUnit.MILLISECONDS);
        } catch (Throwable ignored) {
            // Планировщик закрыт — контур останавливается, это не ошибка.
        }
    }

    private void tick() {
        long step = (decided || ticks > 150) ? STEP_MS : WARMUP_STEP_MS;
        long started = System.nanoTime() / 1_000_000L;
        try {
            runOnce();
        } catch (Throwable t) {
            // Ядро молчит — это НЕ отказ маршрутов. Записать его как отказ
            // значило бы сказать движку, что умерли все узлы разом, и он начал
            // бы метаться по исправной сети.
            Log.i(TAG, "круг пропущен: " + t);
        }
        ticks++;
        // Шаг отсчитывается от НАЧАЛА круга, а не от конца. Иначе объявленный
        // движку шаг и настоящий расходятся на длину круга, и движок считает
        // возраст замеров по неверной мерке — на macOS это ровно так и держало
        // его вечно непрогретым: объявлено шесть секунд, на деле тринадцать.
        long spent = System.nanoTime() / 1_000_000L - started;
        schedule(Math.max(0, step - spent));
    }

    private void runOnce() throws Exception {
        JSONObject proxies = api.proxies().getJSONObject("proxies");

        // Что ядро выбрало сейчас. Сам решающий слой в сеть не ходит и знать
        // этого не может.
        List<String> axes = new ArrayList<>();
        List<String> lanes = new ArrayList<>();
        JSONArray names = proxies.names();
        for (int i = 0; names != null && i < names.length(); i++) {
            String tag = names.getString(i);
            if (tag.startsWith("AX.")) axes.add(tag);
            else if (tag.startsWith("L.")) lanes.add(tag);
        }
        if (axes.isEmpty()) return;

        if (engine == null) {
            createEngine(axes, lanes);
        }

        // Меряем ОСИ, а не каждый узел: внутри оси живого держит ядро, а нам
        // важно, жив ли СПОСОБ пройти.
        //
        // Пробы идут ОДНОВРЕМЕННО. По очереди круг стоил времени самой медленной
        // оси плюс всех остальных: пять осей, у молчащей тайм-аут четыре
        // секунды — и круг растягивался до семи вместо объявленных двух.
        // Замерено на живом телефоне 2026-08-24: первое решение движка пришло
        // через 5 минут 28 секунд после подключения, 49 кругов вхолостую.
        // Движок считает возраст замеров от ОБЪЯВЛЕННОГО шага, поэтому
        // расхождение не просто медлит — оно ещё и врёт ему про время.
        long now = System.nanoTime() / 1_000_000L;
        Map<String, Integer> measured = new HashMap<>();
        List<java.util.concurrent.Callable<Void>> probes = new ArrayList<>();
        for (String axis : axes) {
            if (!axisByTag.containsKey(axis)) continue;
            probes.add(() -> {
                int ms;
                try {
                    ms = api.delay(axis, PROBE_URL, PROBE_TIMEOUT_MS);
                } catch (Throwable t) {
                    ms = -1;
                }
                synchronized (measured) { measured.put(axis, ms); }
                return null;
            });
        }
        try {
            probePool.invokeAll(probes, PROBE_TIMEOUT_MS + 500, TimeUnit.MILLISECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return;
        }

        // Замеры отдаём движку ПОСЛЕДОВАТЕЛЬНО: он живёт за границей в общем
        // слое и не обязан быть готовым к нескольким потокам разом.
        List<String> silent = new ArrayList<>();
        for (String axis : axes) {
            Integer handle = axisByTag.get(axis);
            if (handle == null) continue;
            Integer ms = measured.get(axis);
            if (ms != null && ms >= 0) {
                engine.observeOk(handle, now, ms);
            } else {
                engine.observeTimeout(handle, now);
                silent.add(axis);
            }
        }
        if (!silent.isEmpty()) {
            // Молчащая ось — минус целый способ пройти, а не «чуть хуже».
            Log.w(TAG, "не отвечают оси: " + String.join(", ", silent));
        }

        // Действующий путь берём у ЯДРА, а не у себя: между нашим решением и
        // тем, что реально несёт трафик, лежит запрос, который мог не дойти.
        String currentTag = null;
        JSONObject webLane = proxies.optJSONObject(DEFAULT_LANE);
        if (webLane != null) {
            String chosen = webLane.optString("now", "");
            if (chosen.startsWith("AX.")) currentTag = chosen;
        }
        listener.onPaths(currentTag == null ? null : humanAxis(currentTag.substring(3)),
                axes.size() - silent.size(), axes.size());

        // Какой именно узел внутри оси выбрало ядро. Спрашиваем ЕГО, а не
        // считаем сами: внутри оси выбор делает ядро по своим замерам.
        String routeTag = null;
        if (currentTag != null) {
            JSONObject axisGroup = proxies.optJSONObject(currentTag);
            if (axisGroup != null) {
                String chosenRoute = axisGroup.optString("now", "");
                if (!chosenRoute.isEmpty()) routeTag = chosenRoute;
            }
        }
        listener.onNode(routeTag);

        rescueFromDeadAxis(currentTag, silent, axes);
        applyDecisions(engine.reconcile(now));
    }

    /**
     * Уход с молчащего пути, не дожидаясь движка.
     *
     * <p>Два вопроса, которые легко спутать. «Какой путь ЛУЧШЕ» требует
     * статистики: сравнивать задержки по одной пробе — гадание, и движок
     * справедливо копит замеры, прежде чем решать. «Жив ли путь ВООБЩЕ»
     * статистики не требует: молчит — значит не несёт трафик, и сравнивать
     * тут нечего.
     *
     * <p>Пока эти вопросы решались одним механизмом, было так: подключение
     * поднимает туннель, ядро ставит полосу на первую попавшуюся ось, и если
     * та на этом канале мертва, человек сидит без интернета, глядя на зелёное
     * «Защищено», ПЯТЬ С ПОЛОВИНОЙ МИНУТ — ровно столько на живом телефоне
     * копился порог участия (замер 2026-08-24, 49 кругов вхолостую).
     *
     * <p>Дребезга это не даёт: условие жёсткое — текущая ось молчит два круга
     * подряд, а замена отвечает. Как только движок прогрелся и принял первое
     * решение, спасение отключается: два механизма, независимо решающих
     * «переключиться», и есть источник метаний.
     */
    private void rescueFromDeadAxis(String currentTag, List<String> silent, List<String> axes)
            throws Exception {
        if (decided || currentTag == null) { deadStreak = 0; return; }
        if (!silent.contains(currentTag)) { deadStreak = 0; return; }
        if (++deadStreak < 2) return;

        String alive = null;
        for (String axis : axes) {
            if (!silent.contains(axis) && axisByTag.containsKey(axis)) { alive = axis; break; }
        }
        if (alive == null) {
            // Молчат все. Переставлять некуда, и молчать об этом нельзя:
            // человек обязан знать, что защиты сейчас нет ни по одному пути.
            Log.w(TAG, "молчат все способы пройти — переставлять некуда");
            listener.onDecision("ни один способ пройти не отвечает — проверьте сеть");
            return;
        }
        if (api.select(DEFAULT_LANE, alive)) {
            deadStreak = 0;
            everApplied.add(DEFAULT_LANE);
            announced.add(DEFAULT_LANE);
            Integer handle = axisByTag.get(alive);
            Integer laneHandle = laneByTag.get(DEFAULT_LANE);
            if (handle != null && laneHandle != null) engine.laneApplied(laneHandle, handle);
            String text = "путь «" + humanAxis(currentTag.substring(3)) + "» молчит, ушли на «"
                    + humanAxis(alive.substring(3)) + "»";
            Log.w(TAG, text);
            listener.onDecision(text);
        }
    }

    private void createEngine(List<String> axes, List<String> lanes) throws Exception {
        engine = new MethodEngine(STEP_MS);
        for (String tag : axes) {
            String key = tag.substring("AX.".length());
            int h = engine.addAxis(key, key);
            axisByTag.put(tag, h);
            tagByAxis.put(h, tag);
        }
        for (String tag : lanes) {
            // Полоса прямого выхода движку не отдаётся вовсе: у неё один
            // допустимый исход, и выбирать не из чего.
            if ("L.lan".equals(tag)) continue;
            int h = engine.addLane(tag, tag, Math.min(2, axes.size()));
            laneByTag.put(tag, h);
            tagByLane.put(h, tag);
        }
        restoreState(System.nanoTime() / 1_000_000L);
        Log.i(TAG, "контур запущен: осей " + axes.size() + ", полос " + laneByTag.size());
    }

    private void applyDecisions(JSONObject decision) throws Exception {
        JSONArray actions = decision.optJSONArray("actions");
        if (actions == null) return;

        for (int i = 0; i < actions.length(); i++) {
            JSONObject a = actions.getJSONObject(i);
            String laneTag = tagByLane.get(a.optInt("lane", -1));
            if (laneTag == null) continue;

            int routeHandle = a.optInt("route", -1);
            String axisTag = tagByAxis.get(routeHandle);
            String text = a.optString("text", "");

            if (axisTag == null) {
                // Кандидатов нет. На ХОЛОДНОМ СТАРТЕ это не отказ: движок ещё
                // ничего не измерил, и ответ у него тот же, что при настоящем
                // отказе, а смысл противоположный. Переводить это в блокировку
                // значит убить интернет сразу после подключения при исправном
                // туннеле — так и вышло на macOS при первом живом прогоне.
                if (!everApplied.contains(laneTag)) {
                    Log.i(TAG, "полоса " + laneTag + ": движок ещё греется, выбор оставлен ядру");
                    continue;
                }
                Log.w(TAG, "полоса " + laneTag + ": живых маршрутов нет — " + text);
                listener.onDecision(text);
                continue;
            }

            if (api.select(laneTag, axisTag)) {
                engine.laneApplied(laneByTag.get(laneTag), routeHandle);
                everApplied.add(laneTag);
                decided = true;
                // ПЕРВЫЙ выбор человеку не сообщается: подключение обязано
                // куда-то пойти, и «включаем такой-то путь» отвечает на вопрос,
                // которого никто не задавал. Смысл объяснения — сказать, почему
                // путь сменился САМ.
                if (announced.contains(laneTag)) {
                    listener.onDecision(text);
                } else {
                    announced.add(laneTag);
                }
                Log.i(TAG, laneTag + " -> " + axisTag + ": " + text);
            } else {
                // Ядро отвергло — это защита, а не сбой: увести полосу за
                // пределы её состава нельзя даже нам.
                Log.w(TAG, "ядро отвергло увод полосы " + laneTag + " на " + axisTag);
            }
        }
    }
}
