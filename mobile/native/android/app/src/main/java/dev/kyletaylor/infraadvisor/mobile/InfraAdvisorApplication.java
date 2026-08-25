package dev.kyletaylor.infraadvisor.mobile;

import android.app.Application;
import com.datadog.android.Datadog;
import com.datadog.android.DatadogSite;
import com.datadog.android.core.configuration.Configuration;
import com.datadog.android.privacy.TrackingConsent;
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
        Rum.enable(rumConfiguration);
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

    private static DatadogSite parseSite(String value) {
        try { return DatadogSite.valueOf(value.toUpperCase(java.util.Locale.US)); }
        catch (IllegalArgumentException ignored) { return DatadogSite.US3; }
    }
}
