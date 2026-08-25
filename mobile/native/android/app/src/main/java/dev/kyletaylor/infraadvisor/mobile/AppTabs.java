package dev.kyletaylor.infraadvisor.mobile;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.widget.Button;

/** Shared bottom-tab navigation keeps Chat, Error Lab, and Info consistent across activities. */
public final class AppTabs {
    public enum Destination { CHAT, ERRORS, INFO }

    private AppTabs() {}

    public static void bind(Activity activity, Destination current) {
        bind(activity, R.id.tab_chat, Destination.CHAT, ChatActivity.class, current);
        bind(activity, R.id.tab_errors, Destination.ERRORS, ErrorLabActivity.class, current);
        bind(activity, R.id.tab_info, Destination.INFO, InfoActivity.class, current);
    }

    private static void bind(Activity activity, int id, Destination destination,
                             Class<? extends Activity> activityClass, Destination current) {
        Button button = activity.findViewById(id);
        boolean selected = destination == current;
        button.setEnabled(!selected);
        button.setTextColor(selected ? Color.rgb(99, 44, 166) : Color.rgb(80, 80, 80));
        button.setOnClickListener(view -> {
            Intent intent = new Intent(activity, activityClass);
            intent.addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
            activity.startActivity(intent);
        });
    }
}
