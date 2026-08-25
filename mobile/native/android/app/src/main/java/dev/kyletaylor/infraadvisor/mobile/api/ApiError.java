package dev.kyletaylor.infraadvisor.mobile.api;

import com.android.volley.VolleyError;
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
        return error.getMessage() == null ? "Network request failed" : error.getMessage();
    }
}
