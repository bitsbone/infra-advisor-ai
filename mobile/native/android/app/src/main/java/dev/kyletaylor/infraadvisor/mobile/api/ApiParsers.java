package dev.kyletaylor.infraadvisor.mobile.api;

import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;
import dev.kyletaylor.infraadvisor.mobile.model.QueryResponse;
import dev.kyletaylor.infraadvisor.mobile.model.User;
import java.util.ArrayList;
import java.util.List;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

public final class ApiParsers {
    private ApiParsers() {}

    public static LoginResponse login(JSONObject json) throws JSONException {
        JSONObject value = json.getJSONObject("user");
        User user = new User(value.getString("id"), value.getString("email"), value.getBoolean("is_admin"),
                value.getBoolean("is_service_account"), value.getString("created_at"));
        return new LoginResponse(json.getString("token"), user);
    }

    public static QueryResponse query(JSONObject json) throws JSONException {
        JSONArray sourceArray = json.optJSONArray("sources");
        List<String> sources = new ArrayList<>();
        if (sourceArray != null) for (int i = 0; i < sourceArray.length(); i++) sources.add(sourceArray.getString(i));
        return new QueryResponse(json.getString("answer"), sources, nullable(json, "trace_id"), nullable(json, "span_id"),
                json.getString("session_id"), json.getString("model"));
    }

    private static String nullable(JSONObject json, String key) { return json.isNull(key) ? null : json.optString(key, null); }
}
