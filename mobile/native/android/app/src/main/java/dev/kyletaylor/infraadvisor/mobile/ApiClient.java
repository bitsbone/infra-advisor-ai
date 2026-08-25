package dev.kyletaylor.infraadvisor.mobile;

import android.content.Context;
import com.android.volley.RequestQueue;
import com.android.volley.Request;
import com.android.volley.Response;
import com.android.volley.toolbox.Volley;
import dev.kyletaylor.infraadvisor.mobile.api.ApiParsers;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;
import dev.kyletaylor.infraadvisor.mobile.model.QueryResponse;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationDetail;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationSummary;
import dev.kyletaylor.infraadvisor.mobile.observability.ObservedJsonRequest;
import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import java.util.ArrayList;
import java.util.List;
import org.json.JSONException;
import org.json.JSONObject;

public final class ApiClient {
    public interface Result<T> { void success(T value); void failure(Exception error); }
    public static final String BACKEND_PYTHON = "python";
    public static final String BACKEND_DOTNET = "dotnet";
    private static final int STANDARD_TIMEOUT_MS = 15_000;
    private static final int QUERY_TIMEOUT_MS = 90_000;

    public static final class ModelOptions {
        public final List<String> models;
        public final String defaultModel;
        ModelOptions(List<String> models, String defaultModel) {
            this.models = models;
            this.defaultModel = defaultModel;
        }
    }
    private final String baseUrl;
    private final RequestQueue queue;

    public ApiClient(Context context, String baseUrl) {
        this.baseUrl = baseUrl.replaceAll("/+$", "");
        this.queue = Volley.newRequestQueue(context.getApplicationContext());
    }

    public void login(String email, String password, Result<LoginResponse> result) {
        try {
            JSONObject body = new JSONObject().put("email", email).put("password", password);
            enqueue(Request.Method.POST, "/auth/login", body, Collections.emptyMap(), STANDARD_TIMEOUT_MS,
                    json -> parseLogin(json, result), result::failure);
        } catch (JSONException error) { result.failure(error); }
    }

    public void query(String token, String prompt, String sessionId, String model, String backend, String userId,
                      String conversationId,
                      Result<QueryResponse> result) {
        try {
            JSONObject body = new JSONObject().put("query", prompt).put("session_id", sessionId).put("model", model);
            Map<String, String> headers = new HashMap<>();
            headers.put("Authorization", "Bearer " + token);
            headers.put("X-Session-ID", sessionId);
            headers.put("X-User-ID", userId);
            headers.put("X-Conversation-ID", conversationId);
            enqueue(Request.Method.POST, apiPath(backend, "/query"), body, headers, QUERY_TIMEOUT_MS,
                    json -> parseQuery(json, result), result::failure);
        } catch (JSONException error) { result.failure(error); }
    }

    public void models(String backend, Result<ModelOptions> result) {
        enqueue(Request.Method.GET, apiPath(backend, "/models"), null, Collections.emptyMap(), STANDARD_TIMEOUT_MS,
                json -> {
                    List<String> models = new ArrayList<>();
                    org.json.JSONArray values = json.optJSONArray("models");
                    if (values != null) {
                        for (int index = 0; index < values.length(); index++) {
                            String value = values.optString(index, "");
                            if (!value.isEmpty()) models.add(value);
                        }
                    }
                    if (models.isEmpty()) models.add("gpt-4.1-mini");
                    result.success(new ModelOptions(models, json.optString("default", models.get(0))));
                }, result::failure);
    }

    public void listConversations(String token, Result<List<ConversationSummary>> result) {
        enqueue(Request.Method.GET, "/api/conversations", null, authHeaders(token), STANDARD_TIMEOUT_MS,
                json -> {
                    try { result.success(ApiParsers.conversations(json)); }
                    catch (JSONException error) { result.failure(error); }
                }, result::failure);
    }

    public void conversation(String token, String id, Result<ConversationDetail> result) {
        enqueue(Request.Method.GET, "/api/conversations/" + id, null, authHeaders(token), STANDARD_TIMEOUT_MS,
                json -> {
                    try { result.success(ApiParsers.conversation(json)); }
                    catch (JSONException error) { result.failure(error); }
                }, result::failure);
    }

    public void createConversation(String token, String title, String model, String backend,
                                   Result<ConversationSummary> result) {
        try {
            JSONObject body = new JSONObject().put("title", title).put("model", model).put("backend", backend);
            enqueue(Request.Method.POST, "/api/conversations", body, authHeaders(token), STANDARD_TIMEOUT_MS,
                    json -> {
                        try { result.success(ApiParsers.conversationSummary(json)); }
                        catch (JSONException error) { result.failure(error); }
                    }, result::failure);
        } catch (JSONException error) { result.failure(error); }
    }

    private void enqueue(int method, String path, JSONObject body, Map<String, String> headers, int timeoutMs,
                         Response.Listener<JSONObject> listener, Response.ErrorListener errors) {
        queue.add(new ObservedJsonRequest(method, baseUrl + path, body, headers, timeoutMs, listener, errors));
    }

    static String apiPath(String backend, String route) {
        return (BACKEND_DOTNET.equals(backend) ? "/api-dotnet" : "/api") + route;
    }

    private static Map<String, String> authHeaders(String token) {
        Map<String, String> headers = new HashMap<>();
        headers.put("Authorization", "Bearer " + token);
        return headers;
    }

    private static void parseLogin(JSONObject json, Result<LoginResponse> result) {
        try { result.success(ApiParsers.login(json)); } catch (JSONException error) { result.failure(error); }
    }
    private static void parseQuery(JSONObject json, Result<QueryResponse> result) {
        try { result.success(ApiParsers.query(json)); } catch (JSONException error) { result.failure(error); }
    }
}
