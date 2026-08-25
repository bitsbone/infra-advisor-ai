package dev.kyletaylor.infraadvisor.mobile.model;

public class ConversationSummary {
    public final String id;
    public final String userId;
    public final String title;
    public final String model;
    public final String backend;
    public final int messageCount;

    public ConversationSummary(String id, String userId, String title, String model, String backend, int messageCount) {
        this.id = id;
        this.userId = userId;
        this.title = title;
        this.model = model;
        this.backend = backend;
        this.messageCount = messageCount;
    }
}
