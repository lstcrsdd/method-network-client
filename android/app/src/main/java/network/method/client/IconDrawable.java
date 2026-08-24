package network.method.client;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.BitmapDrawable;
import android.view.View;

/**
 * Значок из {@link IconView} в виде картинки.
 *
 * <p>Нужен там, где Android принимает только {@code Drawable} — например,
 * внутри поля ввода. Рисуем тот же путь, что и обычный значок, чтобы формы в
 * интерфейсе не расходились: значок лупы, нарисованный отдельно «примерно
 * так же», отличался бы толщиной и радиусами, и это видно.
 */
final class IconDrawable extends BitmapDrawable {

    IconDrawable(Context context, int icon, int color, int sizePx) {
        super(context.getResources(), render(context, icon, color, sizePx));
        setBounds(0, 0, sizePx, sizePx);
    }

    private static Bitmap render(Context context, int icon, int color, int size) {
        IconView view = new IconView(context, icon, color);
        int spec = View.MeasureSpec.makeMeasureSpec(size, View.MeasureSpec.EXACTLY);
        view.measure(spec, spec);
        view.layout(0, 0, size, size);
        Bitmap bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
        view.draw(new Canvas(bitmap));
        return bitmap;
    }
}
