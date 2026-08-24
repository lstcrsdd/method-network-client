package network.method.client;

import android.view.View;
import android.view.WindowInsets;

/**
 * Отступы под системные полосы.
 *
 * <p>Высота строки состояния и панели навигации у каждой прошивки своя, а на
 * телефонах с вырезом или «островом» — ещё и зависит от поворота. Зашитый
 * отступ «примерно 24dp» на одном аппарате оставит щель, на другом уведёт
 * заголовок под часы. Спрашиваем систему.
 *
 * <p>Отступ ДОБАВЛЯЕТСЯ к собственному, а не заменяет его: собственный задаёт
 * поля макета, системный — безопасную зону, и путать их значит терять то
 * одно, то другое при повороте.
 */
final class Insets {
    private Insets() {}

    static void pad(View view) {
        final int left = view.getPaddingLeft(), top = view.getPaddingTop();
        final int right = view.getPaddingRight(), bottom = view.getPaddingBottom();
        view.setOnApplyWindowInsetsListener((v, insets) -> {
            android.graphics.Insets bars = insets.getInsets(
                    WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
            v.setPadding(left + bars.left, top + bars.top,
                    right + bars.right, bottom + bars.bottom);
            return insets;
        });
        view.requestApplyInsets();
    }
}
