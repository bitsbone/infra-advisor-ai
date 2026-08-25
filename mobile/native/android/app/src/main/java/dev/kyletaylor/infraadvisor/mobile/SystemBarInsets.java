package dev.kyletaylor.infraadvisor.mobile;

import android.view.View;
import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowInsetsCompat;

/**
 * Keeps activity chrome outside status-bar cutouts and gesture-navigation areas.
 *
 * Android enforces edge-to-edge layout for modern target SDKs. Applying the system-bar insets to
 * the outermost activity view makes the in-layout toolbar and interactive content accessible while
 * preserving edge-to-edge background drawing. Keep screen-specific padding on an inner content view.
 */
public final class SystemBarInsets {
    private SystemBarInsets() {}

    public static void apply(View root) {
        int initialLeft = root.getPaddingLeft();
        int initialTop = root.getPaddingTop();
        int initialRight = root.getPaddingRight();
        int initialBottom = root.getPaddingBottom();
        ViewCompat.setOnApplyWindowInsetsListener(root, (view, windowInsets) -> {
            Insets bars = windowInsets.getInsets(WindowInsetsCompat.Type.systemBars()
                    | WindowInsetsCompat.Type.displayCutout());
            view.setPadding(
                    initialLeft + bars.left,
                    initialTop + bars.top,
                    initialRight + bars.right,
                    initialBottom + bars.bottom);
            return windowInsets;
        });
        ViewCompat.requestApplyInsets(root);
    }
}
