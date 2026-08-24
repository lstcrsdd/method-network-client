package network.method.client;

import android.net.VpnService;
import android.os.Build;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.net.InetAddress;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.List;

final class LibboxRunner {
    private static final String TAG = "MethodLibbox";
    private static boolean setupDone = false;

    private final MethodVpnService service;
    private ParcelFileDescriptor tun;
    private Object commandServer;

    LibboxRunner(MethodVpnService service) {
        this.service = service;
    }

    boolean available() {
        try {
            Class.forName("io.nekohasekai.libbox.Libbox");
            return true;
        } catch (Throwable e) {
            return false;
        }
    }

    void start(String config) throws Exception {
        stop();
        setupLibbox();

        Class<?> libboxClass = Class.forName("io.nekohasekai.libbox.Libbox");
        Class<?> handlerClass = Class.forName("io.nekohasekai.libbox.CommandServerHandler");
        Class<?> platformClass = Class.forName("io.nekohasekai.libbox.PlatformInterface");
        Class<?> overrideOptionsClass = Class.forName("io.nekohasekai.libbox.OverrideOptions");

        Object handler = Proxy.newProxyInstance(
                handlerClass.getClassLoader(),
                new Class[]{handlerClass},
                new HandlerProxy()
        );
        Object platform = Proxy.newProxyInstance(
                platformClass.getClassLoader(),
                new Class[]{platformClass},
                new PlatformProxy()
        );

        commandServer = libboxClass
                .getMethod("newCommandServer", handlerClass, platformClass)
                .invoke(null, handler, platform);
        call(commandServer, "start");
        Object overrideOptions = overrideOptionsClass.getConstructor().newInstance();
        call(commandServer, "startOrReloadService", config, overrideOptions);
        Log.i(TAG, "sing-box service started");
    }

    void stop() {
        if (commandServer != null) {
            try {
                call(commandServer, "closeService");
            } catch (Throwable ignored) {
            }
            try {
                call(commandServer, "close");
            } catch (Throwable ignored) {
            }
            commandServer = null;
        }
        closeTun();
    }

    private final class HandlerProxy implements InvocationHandler {
        @Override
        public Object invoke(Object proxy, Method method, Object[] args) {
            String name = method.getName();
            if ("serviceStop".equals(name)) {
                closeTun();
                return null;
            }
            if ("writeDebugMessage".equals(name) || "writeMessage".equals(name)) {
                Log.d(TAG, args != null && args.length > 0 ? String.valueOf(args[args.length - 1]) : "");
                return null;
            }
            if ("getSystemProxyStatus".equals(name)) return null;
            return defaultValue(method.getReturnType());
        }
    }

    private final class PlatformProxy implements InvocationHandler {
        @Override
        public Object invoke(Object proxy, Method method, Object[] args) throws Throwable {
            String name = method.getName();
            if ("openTun".equals(name)) {
                return openTun(args[0]);
            }
            if ("autoDetectInterfaceControl".equals(name) && args != null && args.length > 0) {
                service.protect(((Number) args[0]).intValue());
                return null;
            }
            if ("usePlatformAutoDetectInterfaceControl".equals(name)) return true;
            if ("useProcFS".equals(name)) return false;
            if ("underNetworkExtension".equals(name)) return false;
            if ("includeAllNetworks".equals(name)) return false;
            if ("readWIFIState".equals(name)) return null;
            if ("localDNSTransport".equals(name)) return null;
            if ("usePlatformShell".equals(name)) return false;
            if ("checkPlatformShell".equals(name)) return null;
            if ("sendNotification".equals(name)) return null;
            if ("systemCertificates".equals(name)) return emptyIterator("io.nekohasekai.libbox.StringIterator");
            if ("startDefaultInterfaceMonitor".equals(name)) {
                startDefaultInterfaceMonitor(args != null && args.length > 0 ? args[0] : null);
                return null;
            }
            if ("closeDefaultInterfaceMonitor".equals(name)) {
                stopDefaultInterfaceMonitor();
                return null;
            }
            if ("startNeighborMonitor".equals(name) || "closeNeighborMonitor".equals(name)) return null;
            if ("clearDNSCache".equals(name)) return null;
            if ("getInterfaces".equals(name)) return networkInterfaces();
            if ("findConnectionOwner".equals(name)) return emptyConnectionOwner();
            if ("lookupUser".equals(name)) return null;
            return defaultValue(method.getReturnType());
        }
    }

    /**
     * Раздельная маршрутизация по приложениям.
     *
     * <p>Три способа промахнуться, и каждый выпускает трафик открытым.
     *
     * <p>ПЕРВЫЙ: смешать режимы. Android не позволяет вызывать
     * {@code addAllowedApplication} и {@code addDisallowedApplication} на одном
     * построителе — второй вызов бросает исключение, туннель не встаёт вовсе.
     * Поэтому режимы разведены жёстко, без «а ещё на всякий случай».
     *
     * <p>ВТОРОЙ: пустой список в режиме «только выбранные». Тогда в туннель не
     * попадает НИЧЕГО, а человек видит «Защищено». Пустой список здесь означает
     * «правил нет» и режим игнорируется — молча защитить всё безопаснее, чем
     * молча выпустить всё.
     *
     * <p>ТРЕТИЙ: удалённое приложение. Пакет, которого больше нет, роняет
     * построитель на {@code NameNotFoundException} — а список правил переживает
     * удаление приложений. Пропускаем такой пакет и говорим об этом в журнал.
     *
     * <p>Себя выводим из туннеля всегда, когда это допустимо режимом: ядро
     * открывает сокеты к узлам от нашего имени, и если завернуть их обратно в
     * туннель, получится петля. В режиме «только выбранные» нас в списке нет,
     * значит мы и так снаружи.
     */
    private void applyAppRules(VpnService.Builder builder) throws Exception {
        AppRules rules = new AppRules(service);
        boolean only = AppRules.MODE_ONLY.equals(rules.mode()) && !rules.isEmpty();

        if (!only) {
            builder.addDisallowedApplication(service.getPackageName());
            if (AppRules.MODE_EXCEPT.equals(rules.mode())) {
                for (String pkg : rules.packages()) {
                    if (pkg.equals(service.getPackageName())) continue;
                    try {
                        builder.addDisallowedApplication(pkg);
                    } catch (Throwable t) {
                        Log.w(TAG, "приложение пропущено (" + pkg + "): " + t);
                    }
                }
            }
            return;
        }

        for (String pkg : rules.packages()) {
            // Себя в туннель не пускаем даже по ошибке: это прямая петля.
            if (pkg.equals(service.getPackageName())) continue;
            try {
                builder.addAllowedApplication(pkg);
            } catch (Throwable t) {
                Log.w(TAG, "приложение пропущено (" + pkg + "): " + t);
            }
        }
    }

    private int openTun(Object options) throws Exception {
        if (VpnService.prepare(service) != null) throw new IllegalStateException("missing vpn permission");

        VpnService.Builder builder = service.newBuilder();
        builder.setSession("Method Network");
        builder.setMtu(intValue(options, "getMTU", "mtu", 1400));
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) builder.setMetered(false);
        applyAppRules(builder);

        addAddresses(builder, value(options, "getInet4Address", "inet4Address"));
        addAddresses(builder, value(options, "getInet6Address", "inet6Address"));
        addDns(builder, value(options, "getDNSServerAddress", "dnsServerAddress"));

        // На Android 13+ можно исключать маршруты, но для v1 достаточно полного туннеля.
        builder.addRoute("0.0.0.0", 0);

        tun = builder.establish();
        if (tun == null) throw new IllegalStateException("VpnService establish returned null");
        Log.i(TAG, "TUN established fd=" + tun.getFd());
        return tun.getFd();
    }

    private void addAddresses(VpnService.Builder builder, Object iterator) {
        forEach(iterator, item -> {
            String address = stringValue(item, "address", "getAddress", null);
            int prefix = intValue(item, "prefix", "getPrefix", -1);
            if (address != null && prefix >= 0) builder.addAddress(address, prefix);
        });
    }

    private void addDns(VpnService.Builder builder, Object iterator) {
        if (iterator == null) return;
        try {
            String boxed = stringValue(iterator, "getValue", "value", null);
            if (boxed != null && !boxed.isEmpty()) {
                builder.addDnsServer(InetAddress.getByName(stripPrefix(boxed)));
                return;
            }
        } catch (Throwable ignored) {
        }
        forEach(iterator, item -> {
            try {
                String address = String.valueOf(item);
                builder.addDnsServer(InetAddress.getByName(stripPrefix(address)));
            } catch (Throwable ignored) {
            }
        });
    }

    private synchronized void setupLibbox() throws Exception {
        if (setupDone) return;
        Class<?> libboxClass = Class.forName("io.nekohasekai.libbox.Libbox");
        Class<?> setupOptionsClass = Class.forName("io.nekohasekai.libbox.SetupOptions");
        Object setupOptions = setupOptionsClass.getConstructor().newInstance();
        java.io.File base = new java.io.File(service.getFilesDir(), "libbox");
        java.io.File work = new java.io.File(base, "work");
        java.io.File temp = new java.io.File(service.getCacheDir(), "libbox");
        //noinspection ResultOfMethodCallIgnored
        work.mkdirs();
        //noinspection ResultOfMethodCallIgnored
        temp.mkdirs();
        call(setupOptions, "setBasePath", base.getAbsolutePath());
        call(setupOptions, "setWorkingPath", work.getAbsolutePath());
        call(setupOptions, "setTempPath", temp.getAbsolutePath());
        call(setupOptions, "setCommandServerListenPort", 0);
        call(setupOptions, "setCommandServerSecret", "method-vpn");
        call(setupOptions, "setLogMaxLines", 300L);
        call(setupOptions, "setDebug", true);
        call(setupOptions, "setFixAndroidStack", true);
        libboxClass.getMethod("setup", setupOptionsClass).invoke(null, setupOptions);
        libboxClass.getMethod("setLocale", String.class).invoke(null, "ru-RU");
        setupDone = true;
        Log.i(TAG, "libbox setup completed");
    }

    private void closeTun() {
        if (tun == null) return;
        try {
            tun.close();
        } catch (Throwable ignored) {
        }
        tun = null;
    }

    private interface ItemConsumer { void accept(Object value) throws Exception; }

    private static void forEach(Object iterator, ItemConsumer consumer) {
        if (iterator == null) return;
        try {
            while ((boolean) call(iterator, "hasNext")) consumer.accept(call(iterator, "next"));
        } catch (Throwable ignored) {
        }
    }

    private static Object emptyIterator(String className) throws Exception {
        Class<?> it = Class.forName(className);
        return Proxy.newProxyInstance(it.getClassLoader(), new Class[]{it}, (proxy, method, args) -> {
            if ("hasNext".equals(method.getName())) return false;
            if ("len".equals(method.getName())) return 0;
            return defaultValue(method.getReturnType());
        });
    }

    private static Object networkInterfaces() throws Exception {
        Class<?> iteratorClass = Class.forName("io.nekohasekai.libbox.NetworkInterfaceIterator");
        List<Object> interfaces = new ArrayList<>();
        Enumeration<java.net.NetworkInterface> all = java.net.NetworkInterface.getNetworkInterfaces();
        if (all != null) {
            for (java.net.NetworkInterface nif : Collections.list(all)) {
                if (!nif.isUp() || nif.isLoopback()) continue;
                String name = nif.getName();
                if (name == null || name.startsWith("tun") || name.startsWith("lo")) continue;
                Object item = newNetworkInterface(nif);
                if (name.startsWith("wlan")) interfaces.add(0, item);
                else interfaces.add(item);
            }
        }
        final int[] index = {0};
        return Proxy.newProxyInstance(iteratorClass.getClassLoader(), new Class[]{iteratorClass}, (proxy, method, args) -> {
            if ("hasNext".equals(method.getName())) return index[0] < interfaces.size();
            if ("next".equals(method.getName())) return interfaces.get(index[0]++);
            return defaultValue(method.getReturnType());
        });
    }

    private static Object newNetworkInterface(java.net.NetworkInterface nif) throws Exception {
        Object item = Class.forName("io.nekohasekai.libbox.NetworkInterface")
                .getConstructor()
                .newInstance();
        String name = nif.getName();
        call(item, "setIndex", nif.getIndex());
        call(item, "setName", name);
        call(item, "setMTU", Math.max(1280, nif.getMTU()));
        call(item, "setType", interfaceType(name));
        call(item, "setFlags", interfaceFlags(nif));
        call(item, "setMetered", false);
        List<String> addresses = new ArrayList<>();
        for (java.net.InterfaceAddress address : nif.getInterfaceAddresses()) {
            InetAddress inet = address.getAddress();
            if (inet == null || inet.isLoopbackAddress()) continue;
            String host = inet.getHostAddress();
            int percent = host.indexOf('%');
            if (percent >= 0) host = host.substring(0, percent);
            addresses.add(host + "/" + address.getNetworkPrefixLength());
        }
        call(item, "setAddresses", stringIterator(addresses));
        call(item, "setDNSServer", stringIterator(Collections.emptyList()));
        return item;
    }

    /**
     * Настоящий монитор сети, а не снимок при запуске.
     *
     * <p>Раньше здесь один раз сообщался текущий интерфейс — и всё. Ядро
     * запоминало его навсегда и продолжало ходить через него после того, как
     * тот исчезал. Замерено на живом телефоне 2026-08-24: выключили Wi-Fi —
     * и ядро ещё минуту долбилось в него, три оси разом отвечали
     * `dial wlan0 (24): network is unreachable`, при исправной сотовой сети.
     * Для телефона это не редкий случай, а обычная дорога домой.
     *
     * <p>Слушаем СЕТЬ ПО УМОЛЧАНИЮ, а не все подряд: ядру важно, куда система
     * шлёт трафик сейчас, а не сколько интерфейсов существует. Наш собственный
     * туннель в этот список не попадает — {@code NET_CAPABILITY_NOT_VPN}, —
     * иначе ядро принялось бы выходить наружу через самого себя.
     */
    private Object interfaceListener;
    private android.net.ConnectivityManager.NetworkCallback networkCallback;
    /** Что мы в последний раз сказали ядру. Чтобы не повторять одно и то же. */
    private String reportedInterface = "";
    private java.util.concurrent.ScheduledExecutorService interfaceWatch;

    private void startDefaultInterfaceMonitor(Object listener) throws Exception {
        interfaceListener = listener;
        reportDefaultInterface(defaultPhysicalInterface());

        android.net.ConnectivityManager cm =
                service.getSystemService(android.net.ConnectivityManager.class);
        if (cm == null || listener == null) return;

        stopDefaultInterfaceMonitor();
        networkCallback = new android.net.ConnectivityManager.NetworkCallback() {
            @Override
            public void onAvailable(android.net.Network network) {
                Log.i(TAG, "сеть появилась");
                update(network);
            }

            @Override
            public void onLinkPropertiesChanged(android.net.Network network,
                                                android.net.LinkProperties props) {
                update(network);
            }

            @Override
            public void onLost(android.net.Network network) {
                // Сеть пропала. Молчать нельзя: без обновления ядро продолжит
                // считать её живой. Ищем, что осталось; не осталось ничего —
                // сообщаем об этом честно, пустым именем.
                try {
                    reportDefaultInterface(defaultPhysicalInterface());
                } catch (Throwable t) {
                    Log.w(TAG, "смена сети не сообщена ядру: " + t);
                }
            }

            private void update(android.net.Network network) {
                // Сеть из события — только СИГНАЛ «что-то изменилось».
                // Спрашивать у неё имя интерфейса нельзя: сетью по умолчанию
                // становится НАШ ЖЕ туннель, и ядро получало приказ выходить
                // наружу через tun0, то есть через самого себя. Поймано на
                // живом телефоне 2026-08-24, в журнале это выглядело как
                // «выход наружу: tun0 (48)» сразу после подъёма туннеля.
                //
                // Физический выход считает defaultPhysicalInterface(): он
                // отбрасывает tun* и петлю и берёт первый живой с адресом.
                try {
                    reportDefaultInterface(defaultPhysicalInterface());
                } catch (Throwable t) {
                    Log.w(TAG, "смена сети не сообщена ядру: " + t);
                }
            }
        };

        android.net.NetworkRequest request = new android.net.NetworkRequest.Builder()
                .addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .removeCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
                .build();
        startInterfaceWatch();
        try {
            cm.registerDefaultNetworkCallback(networkCallback);
            Log.i(TAG, "слежу за сетью по умолчанию");
        } catch (Throwable t) {
            // На некоторых оболочках регистрация по умолчанию недоступна —
            // тогда слушаем по запросу. Хуже, но лучше, чем ничего.
            Log.w(TAG, "слушаем сеть по запросу: " + t);
            cm.registerNetworkCallback(request, networkCallback);
        }
    }

    private void stopDefaultInterfaceMonitor() {
        stopInterfaceWatch();
        if (networkCallback == null) return;
        Log.i(TAG, "слежение за сетью снято");
        try {
            android.net.ConnectivityManager cm =
                    service.getSystemService(android.net.ConnectivityManager.class);
            if (cm != null) cm.unregisterNetworkCallback(networkCallback);
        } catch (Throwable ignored) {
            // Уже снят — не беда.
        }
        networkCallback = null;
    }

    /**
     * Страховка от молчаливой прошивки.
     *
     * <p>Оповещения о смене сети — вещь ненадёжная. Замерено на живом Xiaomi
     * 2026-08-24: {@code registerDefaultNetworkCallback} исправно сработал при
     * запуске и не прислал НИЧЕГО, когда Wi-Fi выключили под работающим
     * туннелем. Ядро осталось с мёртвым wlan0 в руках.
     *
     * <p>Поэтому раз в три секунды сверяем, через что выходим на самом деле.
     * Перечислить интерфейсы стоит доли миллисекунды — несопоставимо дешевле,
     * чем потерять связь и не понять почему. Оповещение оставлено как быстрый
     * сигнал: когда оно работает, переезд занимает миллисекунды, а не секунды.
     */
    private void startInterfaceWatch() {
        stopInterfaceWatch();
        interfaceWatch = java.util.concurrent.Executors.newSingleThreadScheduledExecutor();
        interfaceWatch.scheduleWithFixedDelay(() -> {
            try {
                reportDefaultInterface(defaultPhysicalInterface());
            } catch (Throwable t) {
                Log.w(TAG, "сверка выхода не удалась: " + t);
            }
        }, 3, 3, java.util.concurrent.TimeUnit.SECONDS);
    }

    private void stopInterfaceWatch() {
        if (interfaceWatch == null) return;
        interfaceWatch.shutdownNow();
        interfaceWatch = null;
    }

    /**
     * Сообщить ядру текущий выход наружу. Пусто — выхода нет.
     *
     * <p>Повтор того же интерфейса не отправляем: сторож тикает каждые три
     * секунды, и без этого журнал превратился бы в ленту одинаковых строк, в
     * которой настоящий переезд не найти.
     */
    private void reportDefaultInterface(java.net.NetworkInterface nif) throws Exception {
        Object listener = interfaceListener;
        if (listener == null) return;
        String key = nif == null ? "" : nif.getName() + "/" + nif.getIndex();
        if (key.equals(reportedInterface)) return;
        reportedInterface = key;
        Method update = listener.getClass().getMethod(
                "updateDefaultInterface", String.class, Integer.TYPE, Boolean.TYPE, Boolean.TYPE);
        if (nif == null) {
            Log.w(TAG, "выхода наружу нет");
            update.invoke(listener, "", -1, false, false);
            return;
        }
        Log.i(TAG, "выход наружу: " + nif.getName() + " (" + nif.getIndex() + ")");
        update.invoke(listener, nif.getName(), nif.getIndex(), false, false);
    }

    private static java.net.NetworkInterface defaultPhysicalInterface() throws Exception {
        Enumeration<java.net.NetworkInterface> all = java.net.NetworkInterface.getNetworkInterfaces();
        if (all == null) return null;
        List<java.net.NetworkInterface> candidates = new ArrayList<>();
        for (java.net.NetworkInterface nif : Collections.list(all)) {
            if (!nif.isUp() || nif.isLoopback()) continue;
            String name = nif.getName();
            if (name == null || name.startsWith("tun") || name.startsWith("lo")) continue;
            if (!hasUsableAddress(nif)) continue;
            if (name.startsWith("wlan")) return nif;
            candidates.add(nif);
        }
        return candidates.isEmpty() ? null : candidates.get(0);
    }

    private static boolean hasUsableAddress(java.net.NetworkInterface nif) {
        for (java.net.InterfaceAddress address : nif.getInterfaceAddresses()) {
            InetAddress inet = address.getAddress();
            if (inet != null && !inet.isLoopbackAddress() && !inet.isLinkLocalAddress()) return true;
        }
        return false;
    }

    /**
     * Флаги интерфейса в том виде, в каком их понимает ядро.
     *
     * <p>Это значения Go-шного {@code net.Flags}, а не Linux-овые: ядро
     * написано на Go и сверяет их напрямую. Порядок бит задан там же —
     * up, broadcast, loopback, point-to-point, multicast, running.
     *
     * <p>Без них автоопределение интерфейса находит НОЛЬ пригодных при
     * поднятом Wi-Fi: интерфейс без {@code up} для ядра опущен. Стоило
     * полного отказа связи при исправных узлах — все пять осей разом
     * отвечали «no available network interface», и снаружи это выглядело
     * как «провайдер режет наши серверы».
     */
    private static int interfaceFlags(java.net.NetworkInterface nif) throws Exception {
        final int UP = 1, BROADCAST = 1 << 1, LOOPBACK = 1 << 2,
                POINT_TO_POINT = 1 << 3, MULTICAST = 1 << 4, RUNNING = 1 << 5;
        int flags = 0;
        // Java не различает административное «поднят» и рабочее «несёт
        // трафик», а ядро проверяет оба. Раз isUp() истинно — ставим оба:
        // соврать в другую сторону дороже, интерфейс просто не будет
        // использован.
        if (nif.isUp()) flags |= UP | RUNNING;
        if (nif.isLoopback()) flags |= LOOPBACK;
        if (nif.isPointToPoint()) flags |= POINT_TO_POINT;
        if (nif.supportsMulticast()) flags |= MULTICAST;
        for (java.net.InterfaceAddress a : nif.getInterfaceAddresses()) {
            if (a.getBroadcast() != null) { flags |= BROADCAST; break; }
        }
        return flags;
    }

    private static int interfaceType(String name) throws Exception {
        Class<?> libbox = Class.forName("io.nekohasekai.libbox.Libbox");
        if (name != null && name.startsWith("wlan")) return libbox.getField("InterfaceTypeWIFI").getInt(null);
        if (name != null && (name.startsWith("rmnet") || name.startsWith("ccmni"))) {
            return libbox.getField("InterfaceTypeCellular").getInt(null);
        }
        return libbox.getField("InterfaceTypeOther").getInt(null);
    }

    private static Object stringIterator(List<String> values) throws Exception {
        Class<?> iteratorClass = Class.forName("io.nekohasekai.libbox.StringIterator");
        final int[] index = {0};
        return Proxy.newProxyInstance(iteratorClass.getClassLoader(), new Class[]{iteratorClass}, (proxy, method, args) -> {
            if ("hasNext".equals(method.getName())) return index[0] < values.size();
            if ("len".equals(method.getName())) return values.size();
            if ("next".equals(method.getName())) return values.get(index[0]++);
            return defaultValue(method.getReturnType());
        });
    }

    private static Object emptyConnectionOwner() throws Exception {
        Object owner = Class.forName("io.nekohasekai.libbox.ConnectionOwner")
                .getConstructor()
                .newInstance();
        call(owner, "setUserId", -1);
        call(owner, "setUserName", "");
        call(owner, "setProcessPath", "");
        call(owner, "setAndroidPackageName", "");
        return owner;
    }

    private static Object value(Object target, String getter, String alt) {
        try { return call(target, getter); } catch (Throwable ignored) {}
        try { return fieldOrMethod(target, alt); } catch (Throwable ignored) {}
        return null;
    }

    private static int intValue(Object target, String getter, String alt, int fallback) {
        Object value = value(target, getter, alt);
        if (value instanceof Number) return ((Number) value).intValue();
        return fallback;
    }

    private static String stringValue(Object target, String getter, String alt, String fallback) {
        Object value = value(target, getter, alt);
        return value == null ? fallback : String.valueOf(value);
    }

    private static Object fieldOrMethod(Object target, String name) throws Exception {
        try { return call(target, name); } catch (Throwable ignored) {}
        return target.getClass().getField(name).get(target);
    }

    private static String stripPrefix(String value) {
        int slash = value.indexOf('/');
        return slash >= 0 ? value.substring(0, slash) : value;
    }

    private static Object call(Object target, String name, Object... args) throws Exception {
        Method match = null;
        for (Method m : target.getClass().getMethods()) {
            if (m.getName().equals(name) && m.getParameterTypes().length == args.length) {
                match = m;
                break;
            }
        }
        if (match == null) throw new NoSuchMethodException(name);
        match.setAccessible(true);
        return match.invoke(target, args);
    }

    private static Object defaultValue(Class<?> type) {
        if (type == Void.TYPE) return null;
        if (type == Boolean.TYPE) return false;
        if (type == Integer.TYPE || type == Short.TYPE || type == Byte.TYPE) return 0;
        if (type == Long.TYPE) return 0L;
        if (type == Float.TYPE) return 0f;
        if (type == Double.TYPE) return 0d;
        return null;
    }
}
