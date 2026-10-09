namespace InfraAdvisor.AgentApi.Observability;

/// <summary>
/// One switch shared by every Microsoft.Extensions.AI / MAF decorator. True
/// exports prompts and completions (including attachment URIs) on the
/// generated GenAI spans so DD LLM Observability shows chat/agent content,
/// matching what ddtrace's OpenAI integration records for Python. This is a
/// demo environment; Datadog Sensitive Data Scanner redacts at intake.
/// Custom tags below stay metadata-only regardless.
/// </summary>
public static class TelemetryPrivacy
{
    public const bool EnableSensitiveData = true;

    public static IReadOnlyDictionary<string, object?> SafeFeedbackTags(
        string traceId, string spanId, string rating, string? sessionId)
    {
        // Preserve trace/span correlation and the categorical rating. The
        // client session ID is accepted only to make the exclusion explicit.
        _ = sessionId;
        return new Dictionary<string, object?>
        {
            ["feedback.trace_id"] = traceId,
            ["feedback.span_id"] = spanId,
            ["feedback.rating"] = rating,
        };
    }
}
