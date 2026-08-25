package dev.kyletaylor.infraadvisor.mobile.model;

import java.util.List;

public final class ConversationDetail extends ConversationSummary {
    public final List<ConversationMessage> messages;

    public ConversationDetail(String id, String userId, String title, String model, String backend, int messageCount,
                              List<ConversationMessage> messages) {
        super(id, userId, title, model, backend, messageCount);
        this.messages = messages;
    }
}
