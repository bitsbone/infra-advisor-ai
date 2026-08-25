package dev.kyletaylor.infraadvisor.mobile.observability;

import com.android.volley.NetworkResponse;
import com.android.volley.Response;
import com.android.volley.DefaultRetryPolicy;
import com.android.volley.toolbox.JsonObjectRequest;
import java.net.URI;
import java.util.HashMap;
import java.util.Map;
import org.json.JSONObject;

/**
 * Per-call Volley JSON request with the demo's shared Datadog instrumentation policy.
 *
 * ApiClient creates a new instance for every HTTP operation; the reusable behavior is this class,
 * not a singleton request. Keeping telemetry at this boundary ensures every current and future API
 * call gets the same resource, span, trace propagation, cancellation, and sanitization behavior.
 */
public final class InstrumentedJsonRequest extends JsonObjectRequest {
    private final VolleyTelemetry telemetry;
    private int responseStatus = 200;
    private long responseSize;
    private final Map<String, String> requestHeaders;

    public InstrumentedJsonRequest(int method, String url, JSONObject body, Map<String, String> headers, int timeoutMs,
                               Response.Listener<JSONObject> listener, Response.ErrorListener errorListener) {
        this(new VolleyTelemetry(methodName(method), sanitize(url)), method, url, body, headers, timeoutMs, listener, errorListener);
    }

    private InstrumentedJsonRequest(VolleyTelemetry telemetry, int method, String url, JSONObject body,
                                Map<String, String> headers, int timeoutMs, Response.Listener<JSONObject> listener,
                                Response.ErrorListener errorListener) {

        // Listener wrapping keeps observability independent from ApiClient:
        // callers receive normal Volley callbacks after telemetry is closed.
        super(method, url, body,
                listener,
                error -> { telemetry.failure(error.networkResponse == null ? 0 : error.networkResponse.statusCode, error); errorListener.onErrorResponse(error); });
        this.telemetry = telemetry;
        this.requestHeaders = mergeHeaders(headers, telemetry.headers());

        // Agent queries can legitimately take tens of seconds. Do not let Volley's short
        // default timeout turn a healthy, still-running AI request into a generic failure.
        setRetryPolicy(new DefaultRetryPolicy(timeoutMs, 0, 1f));
    }

    @Override protected Response<JSONObject> parseNetworkResponse(NetworkResponse response) {
        responseStatus = response.statusCode;
        responseSize = response.data == null ? 0 : response.data.length;
        return super.parseNetworkResponse(response);
    }

    @Override protected void deliverResponse(JSONObject response) {
        // parseNetworkResponse runs first, so this is the single success boundary with the
        // actual HTTP status and wire response size. The superclass then calls the app listener.
        telemetry.success(responseStatus, responseSize);
        super.deliverResponse(response);
    }

    @Override public Map<String, String> getHeaders() { return new HashMap<>(requestHeaders); }

    @Override public void cancel() {
        telemetry.cancel();
        super.cancel();
    }

    static String sanitize(String url) {
        // Query strings and fragments can contain user or application data.
        // RUM/APM get the stable route URL only; request bodies are never tags.
        try {
            URI value = URI.create(url);
            return new URI(value.getScheme(), value.getAuthority(), value.getPath(), null, null).toString();
        } catch (Exception ignored) { return url.split("[?#]", 2)[0]; }
    }

    static Map<String, String> mergeHeaders(Map<String, String> applicationHeaders,
                                             Map<String, String> propagationHeaders) {
        // The bearer token remains an HTTP-only value. Propagation headers are merged into a
        // copy and never added to span/RUM attributes or the caller's mutable map.
        Map<String, String> merged = new HashMap<>(applicationHeaders);
        merged.putAll(propagationHeaders);
        return merged;
    }

    private static String methodName(int method) {
        if (method == Method.GET) return "GET";
        if (method == Method.DELETE) return "DELETE";
        if (method == Method.PUT) return "PUT";
        if (method == Method.PATCH) return "PATCH";
        return "POST";
    }
}
