package network.method.client;

import android.app.Activity;
import android.os.Bundle;
import android.view.Window;
import android.widget.Toast;

/**
 * Страница сценариев.
 *
 * <p>Обёртка вокруг {@link ScenariosView}: сама вёрстка списка живёт там, здесь
 * — только оболочка страницы. Раньше список открывался диалогом, и закрыть его
 * можно было лишь нарисованной кнопкой «Готово» — приём чужой для Android, где
 * всё, что листают, закрывается системной «назад».
 */
public final class ScenariosActivity extends Activity {

    /**
     * Сказано ли уже про переподключение.
     *
     * <p>Одного раза достаточно. Повторять на каждую правку — превращать
     * важное сообщение в шум: человек, добавляющий пять правил подряд, увидит
     * его пять раз и перестанет читать на втором.
     */
    private boolean warned;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        setContentView(new ScenariosView(this, new PolicyStore(this), () -> {
            if (warned) return;
            warned = true;
            // Правила попадают в конфиг ядра при ПОДКЛЮЧЕНИИ: живьём у sing-box
            // меняется только выбор внутри группы. Молча сохранить правило и не
            // применить его хуже, чем отказать: список говорит «сделано» при
            // прежнем поведении.
            if (MethodVpnService.status == MethodVpnService.STATUS_CONNECTED) {
                Toast.makeText(this,
                        "правила изменены — переподключитесь, чтобы они заработали",
                        Toast.LENGTH_LONG).show();
            }
        }));
    }
}
