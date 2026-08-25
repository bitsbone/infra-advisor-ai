package dev.kyletaylor.infraadvisor.mobile.model;

import java.util.List;

public final class ConversationMessage {
    public final String id;
    public final String conversationId;
    public final String role;
    public final String content;
    public final List<String> sources;
    public final String traceId;
    public final String spanId;

    public ConversationMessage(String id, String conversationId, String role, String content, List<String> sources,
                               String traceId, String spanId) {
        this.id = id;
        this.conversationId = conversationId;
        this.role = role;
        this.content = content;
        this.sources = sources;
        this.traceId = traceId;
        this.spanId = spanId;
    }
}
