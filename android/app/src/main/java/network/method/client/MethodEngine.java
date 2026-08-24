package network.method.client;

import org.json.JSONObject;

/**
 * Движок выбора маршрута — тот же, что решает на macOS.
 *
 * <p>Живёт в куче на стороне Rust, сюда отдаётся дескриптором. Java обязана
 * его закрыть: в языке со сборщиком мусора об этом легко забыть, поэтому класс
 * сделан {@link AutoCloseable}, а поле обнуляется — повторное закрытие
 * безобидно, а вот двойное освобождение в Rust было бы порчей кучи.
 *
 * <p><b>В движок объявляются ОСИ, а не узлы.</b> Внутри оси живого держит само
 * ядро группой urltest, и это работает, даже если наш контур умрёт. Движок
 * решает то, чего ядро не умеет вовсе, — каким СПОСОБОМ проходить. Разница
 * существенная: три сервера на одной оси убивает одна и та же причина, и
 * перебор между ними при блокировке не даёт ничего.
 */
final class MethodEngine implements AutoCloseable {

    private long handle;

    private static native long nativeCreate(long probeIntervalMs);
    private static native void nativeDestroy(long handle);
    private static native int nativeAddRoute(long handle, String descriptorJson);
    private static native int nativeAddLane(long handle, String descriptorJson);
    private static native void nativeObserve(long handle, int route, long atMs, String outcomeJson);
    private static native String nativeReconcile(long handle, long nowMs);
    private static native void nativeLaneApplied(long handle, int lane, int route);
    private static native byte[] nativeSaveState(long handle, long nowMs);
    private static native void nativeLoadState(long handle, byte[] data, long nowMs, long elapsedMs);

    /**
     * @param probeIntervalMs объявленный шаг проб. Движок считает от него
     *                        возраст замеров, и расхождение с настоящим шагом
     *                        делает уверенность вечно недостаточной — на macOS
     *                        из-за этого движок «грелся» бесконечно.
     */
    MethodEngine(long probeIntervalMs) {
        handle = nativeCreate(probeIntervalMs);
        if (handle == 0) throw new IllegalStateException("движок не создан");
    }

    /** Объявить ось обхода. Возвращает дескриптор оси внутри движка. */
    int addAxis(String id, String axisKey) throws Exception {
        JSONObject d = new JSONObject();
        d.put("id", id);
        d.put("axis", axisKey);
        d.put("node", "—");
        d.put("transport", axisKey);
        return nativeAddRoute(handle, d.toString());
    }

    /**
     * Объявить полосу.
     *
     * @param minAxes минимум независимых осей. Полоса без запаса на другой оси
     *                не имеет запаса вообще.
     */
    int addLane(String id, String title, int minAxes) throws Exception {
        JSONObject d = new JSONObject();
        d.put("id", id);
        d.put("title", title);
        d.put("sla", "browse");
        d.put("minAxes", minAxes);
        return nativeAddLane(handle, d.toString());
    }

    void observeOk(int axis, long atMs, double rttMs) throws Exception {
        JSONObject o = new JSONObject();
        o.put("kind", "ok");
        o.put("rttMs", rttMs);
        nativeObserve(handle, axis, atMs, o.toString());
    }

    void observeTimeout(int axis, long atMs) throws Exception {
        nativeObserve(handle, axis, atMs, "{\"kind\":\"timeout\"}");
    }

    /** Решение: действия и объяснения. */
    JSONObject reconcile(long nowMs) throws Exception {
        return new JSONObject(nativeReconcile(handle, nowMs));
    }

    /**
     * Подтвердить, что переключение состоялось.
     *
     * <p>Между «движок решил» и «ядро переключило» лежит запрос, который может
     * не дойти. Без подтверждения движок замолчал бы ровно тогда, когда
     * переключение не произошло.
     */
    void laneApplied(int lane, int axis) {
        nativeLaneApplied(handle, lane, axis);
    }

    byte[] saveState(long nowMs) {
        return nativeSaveState(handle, nowMs);
    }

    void loadState(byte[] data, long nowMs, long elapsedMs) {
        nativeLoadState(handle, data, nowMs, elapsedMs);
    }

    @Override
    public void close() {
        if (handle != 0) {
            nativeDestroy(handle);
            handle = 0;
        }
    }
}
