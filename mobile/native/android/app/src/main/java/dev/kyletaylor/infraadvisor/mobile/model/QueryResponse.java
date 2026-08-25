package dev.kyletaylor.infraadvisor.mobile.model;

import java.util.List;

public final class QueryResponse {
    public final String answer;
    public final List<String> sources;
    public final String traceId;
    public final String spanId;
    public final String sessionId;
    public final String model;

    public QueryResponse(String answer, List<String> sources, String traceId, String spanId, String sessionId, String model) {
        this.answer = answer;
        this.sources = sources;
        this.traceId = traceId;
        this.spanId = spanId;
        this.sessionId = sessionId;
        this.model = model;
    }
}
