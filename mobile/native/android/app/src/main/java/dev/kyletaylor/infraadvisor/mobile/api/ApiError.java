package dev.kyletaylor.infraadvisor.mobile.api;

import com.android.volley.VolleyError;
import com.android.volley.AuthFailureError;
import com.android.volley.NoConnectionError;
import com.android.volley.TimeoutError;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;

public final class ApiError {
    private ApiError() {}
    public static String message(VolleyError error) {
        if (error.networkResponse != null && error.networkResponse.data != null) {
            try {
                JSONObject body = new JSONObject(new String(error.networkResponse.data, StandardCharsets.UTF_8));
                String detail = body.optString("detail", body.optString("message", ""));
                if (!detail.isEmpty()) return detail;
            } catch (Exception ignored) {}
            return "Request failed (" + error.networkResponse.statusCode + ")";
        }
        if (error instanceof TimeoutError) return "The agent took too long to respond. Try again or choose a different backend.";
        if (error instanceof NoConnectionError) return "Cannot reach the Infra Advisor API. Check the emulator's internet connection and try again.";
        if (error instanceof AuthFailureError) return "The request could not be authenticated. Sign out and sign in again.";
        return error.getMessage() == null ? "Network request failed" : error.getMessage();
    }
}
