namespace InfraAdvisor.AgentApi.Observability;

/// <summary>
/// One fail-closed switch shared by every Microsoft.Extensions.AI / MAF
/// decorator. False keeps prompts, completions, attachment URIs, and SAS
/// query strings out of automatically generated GenAI span attributes.
/// </summary>
public static class TelemetryPrivacy
{
    public const bool EnableSensitiveData = false;

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
