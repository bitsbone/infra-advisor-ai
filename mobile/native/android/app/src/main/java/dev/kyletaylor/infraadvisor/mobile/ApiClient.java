package dev.kyletaylor.infraadvisor.mobile;

import android.content.Context;
import com.android.volley.RequestQueue;
import com.android.volley.Response;
import com.android.volley.toolbox.Volley;
import dev.kyletaylor.infraadvisor.mobile.api.ApiParsers;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;
import dev.kyletaylor.infraadvisor.mobile.model.QueryResponse;
import dev.kyletaylor.infraadvisor.mobile.observability.ObservedJsonRequest;
import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import org.json.JSONException;
import org.json.JSONObject;

public final class ApiClient {
    public interface Result<T> { void success(T value); void failure(Exception error); }
    private final String baseUrl;
    private final RequestQueue queue;

    public ApiClient(Context context, String baseUrl) {
        this.baseUrl = baseUrl.replaceAll("/+$", "");
        this.queue = Volley.newRequestQueue(context.getApplicationContext());
    }

    public void login(String email, String password, Result<LoginResponse> result) {
        try {
            JSONObject body = new JSONObject().put("email", email).put("password", password);
            enqueue("/auth/login", body, Collections.emptyMap(), json -> parseLogin(json, result), result::failure);
        } catch (JSONException error) { result.failure(error); }
    }

    public void query(String token, String prompt, String sessionId, Result<QueryResponse> result) {
        try {
            JSONObject body = new JSONObject().put("query", prompt).put("session_id", sessionId);
            Map<String, String> headers = new HashMap<>();
            headers.put("Authorization", "Bearer " + token);
            enqueue("/api/query", body, headers, json -> parseQuery(json, result), result::failure);
        } catch (JSONException error) { result.failure(error); }
    }

    private void enqueue(String path, JSONObject body, Map<String, String> headers,
                         Response.Listener<JSONObject> listener, Response.ErrorListener errors) {
        queue.add(new ObservedJsonRequest(baseUrl + path, body, headers, listener, errors));
    }

    private static void parseLogin(JSONObject json, Result<LoginResponse> result) {
        try { result.success(ApiParsers.login(json)); } catch (JSONException error) { result.failure(error); }
    }
    private static void parseQuery(JSONObject json, Result<QueryResponse> result) {
        try { result.success(ApiParsers.query(json)); } catch (JSONException error) { result.failure(error); }
    }
}
