package network.method.client;

import android.net.TrafficStats;
import android.os.Process;
import android.util.Log;

import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * Счётчик скорости.
 *
 * <p>Числа берутся у СИСТЕМЫ, а не у ядра. Это не первый выбор, а вывод из
 * проверки: локальный интерфейс ядра отдаёт `/traffic` исправно — код 200,
 * строка в секунду, — но числа в нём нули даже под настоящей загрузкой
 * (проверено на живом телефоне 2026-08-24, 50 МБ через туннель, все замеры
 * `{"up":0,"down":0}`). В сборке libbox для платформенных клиентов счётчик к
 * этому интерфейсу не подключён.
 *
 * <p>Системный счётчик считает трафик НАШЕГО процесса — то есть обмен с узлом
 * вместе с шифрованием и служебными пакетами. Это на несколько процентов
 * больше полезного трафика, и это даже честнее: столько человек реально
 * потратит из мобильного пакета.
 *
 * <p>Считаем разность за такт, а не абсолютные значения: системные счётчики
 * растут с момента загрузки телефона, и без разности карточка показывала бы
 * гигабайты, накопленные за сутки.
 */
final class TrafficMeter {

    private static final String TAG = "MethodTraffic";

    /** Такт. Секунда — тот же ритм, в котором обновляется главный экран. */
    private static final long STEP_MS = 1000;

    interface Listener {
        /** Байт в секунду вниз и вверх. */
        void onTraffic(long downBps, long upBps);
    }

    private final Listener listener;
    private ScheduledExecutorService timer;

    private long lastRx = -1;
    private long lastTx = -1;
    private long lastAt;

    TrafficMeter(Listener listener) {
        this.listener = listener;
    }

    void start() {
        stop();
        timer = Executors.newSingleThreadScheduledExecutor();
        timer.scheduleWithFixedDelay(this::tick, STEP_MS, STEP_MS, TimeUnit.MILLISECONDS);
    }

    void stop() {
        if (timer != null) {
            timer.shutdownNow();
            timer = null;
        }
        lastRx = lastTx = -1;
        // Обнуляем на выходе: замершие на последнем значении цифры читаются
        // как «трафик идёт», когда туннеля уже нет.
        listener.onTraffic(0, 0);
    }

    private void tick() {
        try {
            int uid = Process.myUid();
            long rx = TrafficStats.getUidRxBytes(uid);
            long tx = TrafficStats.getUidTxBytes(uid);
            // Отдельные прошивки объявляют счётчики по процессу неподдержанными.
            // Тогда берём общие: показать трафик всего телефона точнее, чем не
            // показать ничего, а под работающим туннелем разница невелика.
            if (rx == TrafficStats.UNSUPPORTED || tx == TrafficStats.UNSUPPORTED) {
                rx = TrafficStats.getTotalRxBytes();
                tx = TrafficStats.getTotalTxBytes();
            }
            long now = System.nanoTime() / 1_000_000L;

            if (lastRx >= 0 && lastTx >= 0 && now > lastAt) {
                // Делим на фактический промежуток, а не на объявленный шаг:
                // планировщик под нагрузкой опаздывает, и деление на константу
                // завышало бы скорость ровно в тот момент, когда она и так высокая.
                double seconds = (now - lastAt) / 1000.0;
                long down = Math.max(0, Math.round((rx - lastRx) / seconds));
                long up = Math.max(0, Math.round((tx - lastTx) / seconds));
                listener.onTraffic(down, up);
            }
            lastRx = rx;
            lastTx = tx;
            lastAt = now;
        } catch (Throwable t) {
            Log.i(TAG, "замер пропущен: " + t);
        }
    }
}
