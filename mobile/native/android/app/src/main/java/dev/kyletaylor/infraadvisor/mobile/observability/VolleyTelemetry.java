package dev.kyletaylor.infraadvisor.mobile.observability;

import com.datadog.android.rum.GlobalRumMonitor;
import com.datadog.android.rum.RumResourceKind;
import com.datadog.android.rum.RumErrorSource;
import com.datadog.android.rum.RumResourceMethod;
import com.datadog.android.rum.RumMonitor;
import com.datadog.android.trace.GlobalDatadogTracer;
import com.datadog.android.trace.api.span.DatadogSpan;
import com.datadog.android.trace.api.tracer.DatadogTracer;
import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicBoolean;
import kotlin.Unit;
import kotlin.jvm.functions.Function3;

public final class VolleyTelemetry {
    private final String key = UUID.randomUUID().toString();
    private final RumMonitor rum = GlobalRumMonitor.get();
    private final DatadogTracer tracer = GlobalDatadogTracer.get();
    private final DatadogSpan span;
    private final AtomicBoolean finished = new AtomicBoolean(false);
    private final Map<String, String> traceHeaders = new HashMap<>();

    public VolleyTelemetry(String method, String sanitizedUrl) {
        // A RUM resource and a mobile APM span model the same HTTP operation.
        // The resource supplies user-experience context; the span supplies the
        // distributed context that the instrumented backend can continue.
        span = tracer.buildSpan("http.request")
                .withTag("http.method", method)
                .withTag("http.url", sanitizedUrl)
                .start();
        tracer.propagate().inject(span.context(), traceHeaders,
                new Function3<Map<String, String>, String, String, Unit>() {
                    @Override public Unit invoke(Map<String, String> carrier, String name, String value) {
                        carrier.put(name, value);
                        return Unit.INSTANCE;
                    }
                });
        rum.startResource(key, RumResourceMethod.POST, sanitizedUrl, correlationAttributes());
    }

    public Map<String, String> headers() { return Collections.unmodifiableMap(traceHeaders); }

    public void success(int statusCode, long sizeBytes) {
        // Volley can notify multiple layers during delivery/cancellation. The
        // atomic guard makes every terminal path finish RUM and Trace once.
        runOnce(finished, () -> {
            span.setTag("http.status_code", statusCode);
            span.setTag("http.response.body.size", sizeBytes);
            rum.stopResource(key, statusCode, sizeBytes, RumResourceKind.NATIVE, correlationAttributes());
            span.finish();
        });
    }

    public void failure(int statusCode, Throwable error) {
        runOnce(finished, () -> {
            if (statusCode > 0) span.setTag("http.status_code", statusCode);
            span.addThrowable(error);
            rum.stopResourceWithError(key, statusCode > 0 ? statusCode : null,
                    error.getMessage() == null ? "Network request failed" : error.getMessage(),
                    RumErrorSource.NETWORK, error, correlationAttributes());
            span.finish();
        });
    }

    public void cancel() { failure(0, new java.util.concurrent.CancellationException("Volley request cancelled")); }
    public boolean isFinished() { return finished.get(); }

    // Package-visible so the concurrency invariant can be unit tested without initializing
    // the Android SDK. All real terminal work stays inside the guarded callback.
    static boolean runOnce(AtomicBoolean state, Runnable terminalWork) {
        if (!state.compareAndSet(false, true)) return false;
        terminalWork.run();
        return true;
    }

    private Map<String, Object> correlationAttributes() {
        // Datadog's RUM resource schema uses the lower 64-bit decimal trace ID
        // and unsigned span ID for the RUM-to-APM link. Keep these reserved
        // keys isolated here if the adapter is copied to another Volley app.
        Map<String, Object> attributes = new HashMap<>();
        attributes.put("_dd.trace_id", Long.toUnsignedString(span.context().getTraceId().toLong()));
        attributes.put("_dd.span_id", Long.toUnsignedString(span.context().getSpanId()));
        attributes.put("_dd.rule_psr", 1.0d);
        return attributes;
    }
}
