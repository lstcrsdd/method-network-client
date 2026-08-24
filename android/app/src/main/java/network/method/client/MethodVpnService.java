package network.method.client;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Intent;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.net.VpnService;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelFileDescriptor;
import android.os.PowerManager;
import android.os.SystemClock;
import android.util.Log;

import java.io.IOException;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * VPN-служба с упором на «неубиваемость»: держит partial wake lock на время
 * соединения (без него QUIC/Hysteria2 не переживает глубокий сон между окнами
 * обслуживания Doze), следит за сменой сети и реальной валидностью VPN-интерфейса
 * (не просто «процесс жив»), сама переподключается и при повторных неудачах —
 * переключается на другой сервер из каталога (тот же принцип авто-fallback,
 * что и в macOS/Windows-клиентах Method).
 */
public final class MethodVpnService extends VpnService {
    private static final String TAG = "MethodVpnService";
    static final String ACTION_START = "network.method.client.START";
    static final String ACTION_STOP = "network.method.client.STOP";
    static final String EXTRA_NAME = "server_name";
    static final String EXTRA_PROTOCOL = "server_protocol";
    static final String EXTRA_SERVER_ID = "server_id";

    // Статус для UI (один процесс → статический слушатель + последнее значение).
    static final int STATUS_DISCONNECTED = 0, STATUS_CONNECTING = 1, STATUS_CONNECTED = 2, STATUS_ERROR = 3;
    interface StatusListener { void onStatus(int status); }
    static volatile StatusListener statusListener;
    static volatile int status = STATUS_DISCONNECTED;
    /** Что показывать на экране. Пишет контур, читает интерфейс. */
    static volatile String currentPath;
    static volatile int aliveAxes;
    static volatile int totalAxes;
    static volatile String lastReason;
    static volatile long lastReasonAt;

    private static void setStatus(int s) {
        status = s;
        new Handler(Looper.getMainLooper()).post(() -> {
            StatusListener l = statusListener;
            if (l != null) l.onStatus(s);
        });
    }

    private static final int NOTIFICATION_ID = 7001;
    private static final String CHANNEL_ID = "method_vpn";
    private static final long MIN_RECONNECT_INTERVAL_MS = 6_000;
    private static final long HEALTH_CHECK_INTERVAL_MS = 20_000;
    /** Сколько раз подряд считаем «не провалидирован», прежде чем дёргать переподключение —
     * системная валидация VPN-сети иногда просто медленная, не признак реальной проблемы. */
    private static final int UNHEALTHY_CHECKS_BEFORE_RECONNECT = 3;
    private static final int MAX_SAME_SERVER_RETRIES = 2;
    private static final long FAILOVER_COOLDOWN_MS = 90_000;
    private static final int MAX_FAILOVER_CANDIDATES = 6;

    private ParcelFileDescriptor vpnInterface;
    private LibboxRunner libboxRunner;
    private PowerManager.WakeLock wakeLock;
    private ConnectivityManager connectivityManager;
    private ConnectivityManager.NetworkCallback defaultNetworkCallback;
    private ConnectivityManager.NetworkCallback vpnNetworkCallback;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Runnable healthCheckTick = this::checkHealth;

    private final Object lock = new Object();
    private String activeServerId;
    private String activeServerName;
    private boolean hasNetwork = true;
    private boolean vpnValidated = true;
    private int unhealthyChecks = 0;
    private int sameServerRetries = 0;
    private volatile boolean reconnecting = false;
    private long lastReconnectAt = 0;
    private final Map<String, Long> failoverCooldownUntil = new HashMap<>();
    private Network vpnNetwork;

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : null;
        if (ACTION_STOP.equals(action)) {
            worker.execute(() -> { synchronized (lock) { fullStopLocked(); } });
            return START_NOT_STICKY;
        }

        String serverName = intent != null ? intent.getStringExtra(EXTRA_NAME) : "Method Network";
        String protocol = intent != null ? intent.getStringExtra(EXTRA_PROTOCOL) : "VPN";
        String serverId = intent != null ? intent.getStringExtra(EXTRA_SERVER_ID) : null;
        setStatus(STATUS_CONNECTING);
        startForeground(NOTIFICATION_ID, notification(serverName, protocol));
        worker.execute(() -> {
            synchronized (lock) {
                activeServerId = serverId;
                activeServerName = serverName;
                sameServerRetries = 0;
                acquireWakeLockLocked();
                startSingBoxLocked(serverId, serverName);
            }
        });
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        synchronized (lock) { fullStopLocked(); }
        worker.shutdown();
        super.onDestroy();
    }

    Builder newBuilder() {
        return new Builder();
    }

    // ───────────── запуск/остановка sing-box ─────────────

    private void startSingBoxLocked(String serverId, String serverName) {
        stopVpnLocked();
        try {
            if (libboxRunner == null) libboxRunner = new LibboxRunner(this);
            if (!libboxRunner.available()) {
                throw new IllegalStateException("libbox runtime не найден в APK");
            }
            String config = buildLaneConfigLocked();
            libboxRunner.start(config);
            onTunnelUpLocked();
        } catch (Throwable error) {
            appendLog("start error: " + error);
            // Раньше здесь поднимался запасной интерфейс 10.88.0.2/32 без единого
            // маршрута, и сразу выставлялся статус «Подключено». Замысел был
            // безобидный — «не ломать интернет», — но следствие обратное:
            // пользователь видел «Защищено», а трафик шёл мимо туннеля с реальным
            // адресом. Молчаливый отказ, выданный за успех, опаснее честной ошибки:
            // человек в этот момент считает себя защищённым и ведёт себя
            // соответственно.
            //
            // Теперь при любой ошибке запуска ядра туннель не создаётся вовсе,
            // а статус становится ошибочным. Интернет при этом остаётся рабочим:
            // мы просто ничего не перехватываем.
            stopVpnLocked();
            setStatus(STATUS_ERROR);
        }
    }

    /** Управляющий контур. Живёт ровно столько же, сколько туннель. */
    private LaneOrchestrator orchestrator;

    /** Счётчик скорости. Тоже живёт ровно столько же, сколько туннель. */
    private TrafficMeter traffic;

    /** Узел, который сейчас несёт трафик. Пусто — ещё не выбран. */
    public static volatile String currentNode;

    /** Байт в секунду вниз и вверх. Читает главный экран. */
    public static volatile long downBps;
    public static volatile long upBps;

    private void startOrchestratorLocked() {
        stopOrchestratorLocked();
        try {
            ClashApi api = new ClashApi("127.0.0.1:9090", ApiSecret.get(this));
            orchestrator = new LaneOrchestrator(this, api, new LaneOrchestrator.Listener() {
                @Override public void onDecision(String text) {
                    appendLog(text);
                    lastReason = text;
                    lastReasonAt = System.currentTimeMillis();
                }
                @Override public void onPaths(String path, int alive, int total) {
                    currentPath = path;
                    aliveAxes = alive;
                    totalAxes = total;
                }
                @Override public void onNode(String routeTag) {
                    currentNode = routeTag == null ? null : routeTitles.get(routeTag);
                }
            });
            orchestrator.start();

            traffic = new TrafficMeter((down, up) -> {
                downBps = down;
                upBps = up;
            });
            traffic.start();
        } catch (Throwable t) {
            // Без контура туннель всё равно работает: внутри оси живого держит
            // само ядро. Теряется верхний ярус — выбор СПОСОБА пройти, — и об
            // этом надо сказать, а не молчать.
            appendLog("контур выбора не запустился: " + t);
        }
    }

    private void stopOrchestratorLocked() {
        currentPath = null;
        currentNode = null;
        aliveAxes = 0;
        totalAxes = 0;
        downBps = 0;
        upBps = 0;
        if (traffic != null) {
            traffic.stop();
            traffic = null;
        }
        if (orchestrator != null) {
            orchestrator.stop();
            orchestrator = null;
        }
    }

    private void onTunnelUpLocked() {
        setStatus(STATUS_CONNECTED);
        startOrchestratorLocked();
        unhealthyChecks = 0;
        registerNetworkCallbacksLocked();
        scheduleHealthCheck();
    }

    private void stopVpnLocked() {
        stopOrchestratorLocked();
        if (libboxRunner != null) libboxRunner.stop();
        if (vpnInterface != null) {
            try { vpnInterface.close(); } catch (IOException ignored) { }
            vpnInterface = null;
        }
    }

    private void fullStopLocked() {
        unregisterNetworkCallbacksLocked();
        mainHandler.removeCallbacks(healthCheckTick);
        stopVpnLocked();
        releaseWakeLockLocked();
        setStatus(STATUS_DISCONNECTED);
        stopSelf();
    }

    // ───────────── WakeLock — без него туннель не переживает глубокий сон ─────────────

    private void acquireWakeLockLocked() {
        if (wakeLock != null && wakeLock.isHeld()) return;
        PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
        if (pm == null) return;
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "network.method.vpn:tunnel");
        wakeLock.setReferenceCounted(false);
        wakeLock.acquire();
    }

    private void releaseWakeLockLocked() {
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        wakeLock = null;
    }

    // ───────────── сеть: реакция на смену сети + реальная валидность туннеля ─────────────

    private void registerNetworkCallbacksLocked() {
        if (connectivityManager == null) {
            connectivityManager = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
        }
        if (connectivityManager == null) return;
        unregisterNetworkCallbacksLocked();

        hasNetwork = true;
        defaultNetworkCallback = new ConnectivityManager.NetworkCallback() {
            @Override public void onLost(Network network) {
                hasNetwork = false;
            }
            @Override public void onAvailable(Network network) {
                boolean wasLost = !hasNetwork;
                hasNetwork = true;
                if (wasLost) triggerReconnect("сеть восстановлена после потери");
            }
        };
        try {
            connectivityManager.registerDefaultNetworkCallback(defaultNetworkCallback);
        } catch (Throwable e) {
            appendLog("registerDefaultNetworkCallback failed: " + e);
        }

        vpnValidated = true;
        unhealthyChecks = 0;
        vpnNetworkCallback = new ConnectivityManager.NetworkCallback() {
            @Override public void onCapabilitiesChanged(Network network, NetworkCapabilities caps) {
                vpnNetwork = network;
                vpnValidated = caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);
            }
            @Override public void onLost(Network network) {
                if (network.equals(vpnNetwork)) triggerReconnect("VPN-сеть пропала");
            }
        };
        try {
            NetworkRequest request = new NetworkRequest.Builder()
                    .addTransportType(NetworkCapabilities.TRANSPORT_VPN)
                    .removeCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
                    .build();
            connectivityManager.registerNetworkCallback(request, vpnNetworkCallback);
        } catch (Throwable e) {
            appendLog("registerNetworkCallback(VPN) failed: " + e);
        }
    }

    private void unregisterNetworkCallbacksLocked() {
        if (connectivityManager == null) return;
        if (defaultNetworkCallback != null) {
            try { connectivityManager.unregisterNetworkCallback(defaultNetworkCallback); } catch (Throwable ignored) { }
            defaultNetworkCallback = null;
        }
        if (vpnNetworkCallback != null) {
            try { connectivityManager.unregisterNetworkCallback(vpnNetworkCallback); } catch (Throwable ignored) { }
            vpnNetworkCallback = null;
        }
        vpnNetwork = null;
    }

    private void scheduleHealthCheck() {
        mainHandler.removeCallbacks(healthCheckTick);
        mainHandler.postDelayed(healthCheckTick, HEALTH_CHECK_INTERVAL_MS);
    }

    /** Периодическая проверка: несколько раз подряд без валидации — реальная проблема. */
    private void checkHealth() {
        if (status != STATUS_CONNECTED) return;
        if (vpnValidated) {
            unhealthyChecks = 0;
        } else {
            unhealthyChecks++;
            if (unhealthyChecks >= UNHEALTHY_CHECKS_BEFORE_RECONNECT) {
                unhealthyChecks = 0;
                triggerReconnect("VPN-сеть невалидна " + UNHEALTHY_CHECKS_BEFORE_RECONNECT + " проверки подряд");
                return;
            }
        }
        scheduleHealthCheck();
    }

    // ───────────── переподключение / авто-переключение сервера ─────────────

    private void triggerReconnect(String reason) {
        if (reconnecting) return;
        long now = SystemClock.elapsedRealtime();
        if (now - lastReconnectAt < MIN_RECONNECT_INTERVAL_MS) return;
        lastReconnectAt = now;
        reconnecting = true;
        appendLog("reconnect: " + reason);
        setStatus(STATUS_CONNECTING);
        worker.execute(() -> {
            try {
                synchronized (lock) { performReconnectLocked(); }
            } finally {
                reconnecting = false;
            }
        });
    }

    private void performReconnectLocked() {
        sameServerRetries++;
        if (sameServerRetries > MAX_SAME_SERVER_RETRIES) {
            // Перебирать серверы здесь БОЛЬШЕ НЕ НАДО, и это не упрощение.
            // Живого внутри оси держит само ядро группой urltest, а выбор
            // ОСИ обхода принадлежит движку этажом выше. Служба, которая
            // сама тасует серверы, спорила бы с обоими: три сервера на одной
            // оси убивает одна и та же причина, и перебор между ними при
            // блокировке не даёт ничего.
            appendLog("ядро не поднимается " + sameServerRetries + " раз подряд — сдаёмся честно");
            setStatus(STATUS_ERROR);
            return;
        }
        startSingBoxLocked(activeServerId, activeServerName);
    }

    /**
     * Конфиг для ядра — из ОБЩЕГО СЛОЯ на Rust.
     *
     * Тот же код собирает конфиг на macOS и Windows. Здесь не должно быть ни
     * строчки собственной сборки: именно из-за трёх отдельных реализаций у
     * Windows до сих пор нет Trojan и Shadowsocks — двух осей обхода из пяти.
     */
    private String buildLaneConfigLocked() throws Exception {
        if (!MethodCore.isAvailable()) {
            throw new IllegalStateException("общий слой недоступен: библиотека не загрузилась");
        }
        Store store = new Store(this);
        String profiles = store.profilesJson();
        if (store.profileCount() == 0) {
            throw new IllegalStateException("серверов нет: добавьте ссылку или подписку");
        }
        // Секрет локального API — случайный на установку. Раньше в клиентах
        // стояла вкомпилированная строка, одинаковая у всех: право переключать
        // маршруты и видеть список соединений было у любого приложения,
        // прочитавшего наш открытый код.
        String secret = ApiSecret.get(this);
        // Стек TUN назначает КЛИЕНТ, а не общий слой: разница платформенная.
        // `system` разбирает пакеты средствами ядра ОС и быстрее, но требует
        // системных прав — на macOS их даёт привилегированный демон. На Android
        // приложению перехватывать TCP не дают, поэтому нужен `gvisor`,
        // разбирающий пакеты внутри процесса.
        //
        // Ошибка здесь не похожа на ошибку: с `system` туннель поднимается,
        // рисует «Защищено», UDP частично ходит и DNS-запросы видны в журнале,
        // а TCP не доходит до ядра ВОВСЕ — ни строки о соединении. Читается как
        // «узлы не отвечают». Стоило часа поисков на живом телефоне 2026-08-24.
        String options = "{\"clashSecret\":\"" + secret + "\",\"tunStack\":\"gvisor\"}";
        // Политику берём пользовательскую: без неё сценарии человека просто не
        // доедут до ядра, а он будет видеть их в списке и считать, что они
        // работают.
        String policy = new PolicyStore(this).json();
        String plan = MethodCore.buildLanes(profiles, policy.isEmpty() ? null : policy, options);
        org.json.JSONObject planJson = new org.json.JSONObject(plan);
        rememberRoutes(planJson.optJSONObject("routes"));
        org.json.JSONObject config = planJson.getJSONObject("config");
        // Пишем в журнал ровно то, чем ядро будет разбирать пакеты. Секретов
        // здесь нет — только описание туннеля. Неверный стек не проявляется как
        // ошибка: туннель встаёт, UDP частично ходит, а TCP не доходит вовсе.
        Log.i(TAG, "туннель: " + config.getJSONArray("inbounds").getJSONObject(0));
        return config.toString();
    }

    /**
     * Тег маршрута → как назвать узел человеку.
     *
     * <p>Ядро на вопрос «что сейчас несёт трафик» отвечает тегом вида «R6» —
     * внутренним именем, которое человеку не говорит ничего. Соответствие
     * тегов профилям знает общий слой; здесь мы его просто запоминаем в момент
     * сборки конфига, чтобы потом было чем подписать выбранный путь.
     */
    static volatile java.util.Map<String, String> routeTitles = new java.util.HashMap<>();

    private void rememberRoutes(org.json.JSONObject routes) {
        java.util.Map<String, String> out = new java.util.HashMap<>();
        if (routes != null) {
            for (java.util.Iterator<String> it = routes.keys(); it.hasNext(); ) {
                String tag = it.next();
                org.json.JSONObject p = routes.optJSONObject(tag);
                if (p == null) continue;
                out.put(tag, p.optString("name") + " · " + p.optString("host")
                        + ":" + p.optInt("port"));
            }
        }
        routeTitles = out;
    }

    private void updateNotification(String serverName, String protocol) {
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager != null) manager.notify(NOTIFICATION_ID, notification(serverName, protocol));
    }

    private void appendLog(String line) {
        Log.i(TAG, line);
        try {
            java.io.File dir = new java.io.File(getFilesDir(), "logs");
            //noinspection ResultOfMethodCallIgnored
            dir.mkdirs();
            java.io.FileWriter writer = new java.io.FileWriter(new java.io.File(dir, "method-vpn.log"), true);
            writer.write(System.currentTimeMillis() + " " + line + "\n");
            writer.close();
        } catch (Throwable ignored) {
        }
    }

    private Notification notification(String serverName, String protocol) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID,
                    "Method VPN",
                    NotificationManager.IMPORTANCE_LOW
            );
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) manager.createNotificationChannel(channel);
        }

        Intent open = new Intent(this, MainActivity.class);
        PendingIntent pending = PendingIntent.getActivity(
                this,
                0,
                open,
                Build.VERSION.SDK_INT >= Build.VERSION_CODES.M ? PendingIntent.FLAG_IMMUTABLE : 0
        );

        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);

        return builder
                .setSmallIcon(R.drawable.ic_stat_method)
                .setContentTitle("Method Network")
                .setContentText(serverName + " · " + protocol)
                .setContentIntent(pending)
                .setOngoing(true)
                .build();
    }
}
