using System.Threading;

namespace InfraAdvisor.AgentApi.Services;

// Datadog's LLM Observability session/conversation grouping for
// OTel-based instrumentation requires gen_ai.conversation.id to be set on
// EVERY gen_ai span within the trace (not just the root — see
// https://docs.datadoghq.com/llm_observability/instrument/otel_instrumentation/#session-and-conversation).
// M.E.AI's/MAF's .UseOpenTelemetry() decorators emit those spans
// (invoke_agent, chat, embeddings) themselves, so there's no call site to
// pass sessionId into directly. Instead, RunAgentAsync/RunAgentStreamingAsync
// stash it here at request entry, and the ActivityListener in Program.cs
// (same pattern as AgentSpanContext) reads it back in ActivityStarted to
// stamp the tag on every span as it's created. AsyncLocal scoping clears it
// automatically between requests.
public static class AmbientSessionContext
{
    private static readonly AsyncLocal<string?> _sessionId = new();

    public static string? Current => _sessionId.Value;

    public static void Set(string sessionId) => _sessionId.Value = sessionId;
}
