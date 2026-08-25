package dev.kyletaylor.infraadvisor.mobile;

import android.app.Application;
import com.datadog.android.Datadog;
import com.datadog.android.DatadogSite;
import com.datadog.android.core.configuration.Configuration;
import com.datadog.android.privacy.TrackingConsent;
import com.datadog.android.log.Logger;
import com.datadog.android.log.Logs;
import com.datadog.android.log.LogsConfiguration;
import com.datadog.android.rum.Rum;
import com.datadog.android.rum.RumConfiguration;
import com.datadog.android.rum.tracking.ActivityViewTrackingStrategy;
import com.datadog.android.sessionreplay.SessionReplay;
import com.datadog.android.sessionreplay.SessionReplayConfiguration;
import com.datadog.android.sessionreplay.TextAndInputPrivacy;
import com.datadog.android.trace.DatadogTracing;
import com.datadog.android.trace.GlobalDatadogTracer;
import com.datadog.android.trace.Trace;
import com.datadog.android.trace.TraceConfiguration;
import com.datadog.android.trace.TracingHeaderType;
import java.net.URI;
import java.util.Arrays;
import java.util.EnumSet;

public final class InfraAdvisorApplication extends Application {
    private final Session session = new Session();
    private ApiClient apiClient;
    private Logger demoLogger;

    @Override public void onCreate() {
        super.onCreate();
        // Core owns shared metadata and upload policy. The client token is safe
        // for a shipped app; a Datadog API key must never be embedded here.
        Configuration configuration = new Configuration.Builder(
                BuildConfig.DD_CLIENT_TOKEN, BuildConfig.DD_ENV, BuildConfig.BUILD_TYPE, BuildConfig.DD_SERVICE)
                .useSite(parseSite(BuildConfig.DD_SITE))
                .setFirstPartyHosts(Arrays.asList(URI.create(BuildConfig.API_BASE_URL).getHost()))
                .build();
        Datadog.initialize(this, configuration, TrackingConsent.GRANTED);

        RumConfiguration rumConfiguration = new RumConfiguration.Builder(BuildConfig.DD_RUM_APPLICATION_ID)
                .setSessionSampleRate(100f)
                .trackUserInteractions()
                .useViewTrackingStrategy(new ActivityViewTrackingStrategy(true))
                .build();
        // Enabling RUM also installs Datadog's uncaught Java exception collection. This app has no
        // application C/C++ code, so the optional NDK crash-reporting module is intentionally absent.
        Rum.enable(rumConfiguration);
        // Logs is a separate SDK feature. This logger sends every fixed demo event and bundles
        // it with active RUM/trace context. Callers must never pass credentials or payload data.
        Logs.enable(new LogsConfiguration.Builder().build());
        demoLogger = new Logger.Builder()
                .setService(BuildConfig.DD_SERVICE)
                .setName("infra-advisor-demo")
                .setNetworkInfoEnabled(true)
                .setBundleWithRumEnabled(true)
                .setBundleWithTraceEnabled(true)
                .setRemoteSampleRate(100f)
                .build();
        demoLogger.i("Infra Advisor mobile observability initialized", null, demoAttributes("app_started"));
        // Record every sampled demo RUM session. Session Replay keeps its privacy defaults;
        // credentials, JWTs, prompts, and payloads are never added as telemetry attributes.
        SessionReplay.enable(new SessionReplayConfiguration.Builder(100f)
                .setTextAndInputPrivacy(TextAndInputPrivacy.MASK_SENSITIVE_INPUTS)
                .build());
        Trace.enable(new TraceConfiguration.Builder().build());
        // Volley is not auto-instrumented by Datadog. Register one tracer for
        // the adapter below and ask it to emit both Datadog and W3C headers so
        // either propagation style can continue the trace on the backend.
        GlobalDatadogTracer.registerIfAbsent(DatadogTracing.newTracerBuilder(Datadog.getInstance())
                .withServiceName(BuildConfig.DD_SERVICE)
                .withSampleRate(BuildConfig.DD_TRACE_SAMPLE_RATE)
                .withTracingHeadersTypes(EnumSet.of(TracingHeaderType.DATADOG, TracingHeaderType.TRACECONTEXT))
                .setBundleWithRumEnabled(true)
                .build());
        apiClient = new ApiClient(this, BuildConfig.API_BASE_URL);
    }

    public Session session() { return session; }
    public ApiClient api() { return apiClient; }
    public Logger logger() { return demoLogger; }

    /** Returns safe, fixed attributes for educational events. Never add user or request data. */
    public static java.util.Map<String, Object> demoAttributes(String signal) {
        java.util.Map<String, Object> attributes = new java.util.HashMap<>();
        attributes.put("demo.signal", signal);
        attributes.put("demo.platform", "android");
        attributes.put("demo.intentional", true);
        return attributes;
    }

    private static DatadogSite parseSite(String value) {
        try { return DatadogSite.valueOf(value.toUpperCase(java.util.Locale.US)); }
        catch (IllegalArgumentException ignored) { return DatadogSite.US3; }
    }
}
