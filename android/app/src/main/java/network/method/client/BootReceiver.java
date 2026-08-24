package network.method.client;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.net.VpnService;
import android.util.Log;

/**
 * Подъём туннеля после перезагрузки телефона.
 *
 * <p>Только если человек этого попросил в настройках. Клиент, который сам себя
 * включает без спроса, — это не забота, а самоуправство: VPN меняет весь
 * сетевой путь устройства, и решение о нём принимает владелец.
 *
 * <p>Разрешение на туннель после перезагрузки НЕ спрашивается заново — Android
 * помнит его, — но если оно не выдавалось вовсе, тихо ничего не делаем:
 * показать системный диалог из загрузки некуда, а поднять туннель без
 * разрешения нельзя.
 */
public final class BootReceiver extends BroadcastReceiver {

    private static final String TAG = "MethodBoot";

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent == null ? null : intent.getAction();
        if (!Intent.ACTION_BOOT_COMPLETED.equals(action)
                && !"android.intent.action.QUICKBOOT_POWERON".equals(action)) {
            return;
        }
        if (!SettingsActivity.autostart(context)) return;
        if (new Store(context).profileCount() == 0) {
            Log.i(TAG, "автозапуск пропущен: серверов нет");
            return;
        }
        if (VpnService.prepare(context) != null) {
            Log.i(TAG, "автозапуск пропущен: разрешение на туннель не выдавалось");
            return;
        }
        try {
            Intent start = new Intent(context, MethodVpnService.class);
            start.setAction(MethodVpnService.ACTION_START);
            context.startForegroundService(start);
            Log.i(TAG, "туннель поднимается после перезагрузки");
        } catch (Throwable t) {
            Log.w(TAG, "автозапуск не удался: " + t);
        }
    }
}
