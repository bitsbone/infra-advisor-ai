package dev.kyletaylor.infraadvisor.mobile.api;

import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;
import dev.kyletaylor.infraadvisor.mobile.model.QueryResponse;
import dev.kyletaylor.infraadvisor.mobile.model.User;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationDetail;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationMessage;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationSummary;
import java.util.ArrayList;
import java.util.List;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

public final class ApiParsers {
    private ApiParsers() {}

    public static LoginResponse login(JSONObject json) throws JSONException {
        JSONObject value = json.getJSONObject("user");
        User user = new User(value.getString("id"), value.getString("email"), value.getBoolean("is_admin"),
                value.getBoolean("is_service_account"), value.getString("created_at"));
        return new LoginResponse(json.getString("token"), user);
    }

    public static QueryResponse query(JSONObject json) throws JSONException {
        JSONArray sourceArray = json.optJSONArray("sources");
        List<String> sources = new ArrayList<>();
        if (sourceArray != null) for (int i = 0; i < sourceArray.length(); i++) sources.add(sourceArray.getString(i));
        return new QueryResponse(json.getString("answer"), sources, nullable(json, "trace_id"), nullable(json, "span_id"),
                json.getString("session_id"), json.getString("model"));
    }

    public static ConversationSummary conversationSummary(JSONObject json) throws JSONException {
        return new ConversationSummary(
                json.getString("id"),
                json.getString("user_id"),
                json.getString("title"),
                nullable(json, "model"),
                nullable(json, "backend"),
                json.optInt("message_count", 0));
    }

    public static List<ConversationSummary> conversations(JSONObject json) throws JSONException {
        JSONArray values = json.optJSONArray("conversations");
        List<ConversationSummary> conversations = new ArrayList<>();
        if (values != null) {
            for (int index = 0; index < values.length(); index++) {
                conversations.add(conversationSummary(values.getJSONObject(index)));
            }
        }
        return conversations;
    }

    public static ConversationDetail conversation(JSONObject json) throws JSONException {
        ConversationSummary summary = conversationSummary(json);
        JSONArray values = json.optJSONArray("messages");
        List<ConversationMessage> messages = new ArrayList<>();
        if (values != null) {
            for (int index = 0; index < values.length(); index++) {
                JSONObject value = values.getJSONObject(index);
                JSONArray sourceValues = value.optJSONArray("sources");
                List<String> sources = new ArrayList<>();
                if (sourceValues != null) {
                    for (int sourceIndex = 0; sourceIndex < sourceValues.length(); sourceIndex++) {
                        sources.add(sourceValues.getString(sourceIndex));
                    }
                }
                messages.add(new ConversationMessage(
                        value.getString("id"), value.getString("conversation_id"), value.getString("role"),
                        value.getString("content"), sources, nullable(value, "trace_id"), nullable(value, "span_id")));
            }
        }
        return new ConversationDetail(summary.id, summary.userId, summary.title, summary.model, summary.backend,
                summary.messageCount, messages);
    }

    private static String nullable(JSONObject json, String key) { return json.isNull(key) ? null : json.optString(key, null); }
}
