package dev.kyletaylor.infraadvisor.mobile.api;

import static org.junit.Assert.assertEquals;
import com.android.volley.NetworkResponse;
import com.android.volley.VolleyError;
import com.android.volley.TimeoutError;
import java.nio.charset.StandardCharsets;
import java.util.Collections;
import org.junit.Test;

public final class ApiErrorTest {
    @Test public void returnsBackendDetail() {
        byte[] body = "{\"detail\":\"Invalid email or password\"}".getBytes(StandardCharsets.UTF_8);
        VolleyError error = new VolleyError(new NetworkResponse(401, body, Collections.emptyMap(), false, 0));
        assertEquals("Invalid email or password", ApiError.message(error));
    }

    @Test public void explainsAgentTimeout() {
        assertEquals(
                "The agent took too long to respond. Try again or choose a different backend.",
                ApiError.message(new TimeoutError())
        );
    }
}
