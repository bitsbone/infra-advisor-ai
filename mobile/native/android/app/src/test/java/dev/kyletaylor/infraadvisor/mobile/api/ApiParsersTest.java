package dev.kyletaylor.infraadvisor.mobile.api;

import static org.junit.Assert.*;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;
import dev.kyletaylor.infraadvisor.mobile.model.QueryResponse;
import org.json.JSONObject;
import org.junit.Test;

public final class ApiParsersTest {
    @Test public void parsesLoginContract() throws Exception {
        LoginResponse value = ApiParsers.login(new JSONObject("{\"token\":\"jwt\",\"user\":{\"id\":\"1\",\"email\":\"demo@datadoghq.com\",\"is_admin\":false,\"is_service_account\":false,\"created_at\":\"2026-01-01\"}}"));
        assertEquals("jwt", value.token);
        assertEquals("1", value.user.id);
    }
    @Test public void parsesQueryAndTraceMetadata() throws Exception {
        QueryResponse value = ApiParsers.query(new JSONObject("{\"answer\":\"ok\",\"sources\":[\"tool\"],\"trace_id\":\"42\",\"span_id\":\"7\",\"session_id\":\"s\",\"model\":\"gpt\"}"));
        assertEquals("42", value.traceId);
        assertEquals("tool", value.sources.get(0));
    }
}
