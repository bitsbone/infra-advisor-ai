package dev.kyletaylor.infraadvisor.mobile.observability;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.Test;

public final class VolleyTelemetryTest {
    @Test public void terminalWorkRunsExactlyOnce() {
        AtomicBoolean state = new AtomicBoolean(false);
        AtomicInteger completions = new AtomicInteger(0);

        assertTrue(VolleyTelemetry.runOnce(state, completions::incrementAndGet));
        assertFalse(VolleyTelemetry.runOnce(state, completions::incrementAndGet));
        assertEquals(1, completions.get());
    }

    @Test public void traceHeadersAreMergedWithoutMutatingCallerHeaders() {
        Map<String, String> application = new HashMap<>();
        application.put("Authorization", "Bearer example");
        Map<String, String> propagation = Map.of(
                "traceparent", "00-example",
                "x-datadog-trace-id", "123"
        );

        Map<String, String> merged = InstrumentedJsonRequest.mergeHeaders(application, propagation);

        assertEquals("Bearer example", merged.get("Authorization"));
        assertEquals("00-example", merged.get("traceparent"));
        assertEquals("123", merged.get("x-datadog-trace-id"));
        assertEquals(1, application.size());
    }

    @Test public void telemetryUrlDropsQueryAndFragment() {
        assertEquals(
                "https://example.test/api/query",
                InstrumentedJsonRequest.sanitize("https://example.test/api/query?token=secret#answer")
        );
    }
}
