package dev.kyletaylor.infraadvisor.mobile.api;

import static org.junit.Assert.*;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;
import dev.kyletaylor.infraadvisor.mobile.model.QueryResponse;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationDetail;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationSummary;
import java.util.List;
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
    @Test public void parsesConversationListAndMessages() throws Exception {
        List<ConversationSummary> summaries = ApiParsers.conversations(new JSONObject("{\"conversations\":[{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Flood plan\",\"model\":\"gpt\",\"backend\":\"dotnet\",\"message_count\":2}]}"));
        assertEquals("dotnet", summaries.get(0).backend);
        ConversationDetail detail = ApiParsers.conversation(new JSONObject("{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Flood plan\",\"model\":\"gpt\",\"backend\":\"dotnet\",\"message_count\":2,\"messages\":[{\"id\":\"m1\",\"conversation_id\":\"c1\",\"role\":\"assistant\",\"content\":\"answer\",\"sources\":[\"search\"],\"trace_id\":\"42\",\"span_id\":\"7\"}]}"));
        assertEquals("answer", detail.messages.get(0).content);
        assertEquals("search", detail.messages.get(0).sources.get(0));
    }
}
