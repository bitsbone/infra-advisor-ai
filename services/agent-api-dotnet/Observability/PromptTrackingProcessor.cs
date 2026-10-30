using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTelemetry;

namespace InfraAdvisor.AgentApi.Observability;

// Stamps prompt metadata on invoke_agent and chat spans:
//   - prompt.version: a content-derived version, so prompt changes correlate
//     with behavior without exporting the template as a plain tag.
//   - _dd.ml_obs.prompt_tracking: DD LLM Observability's prompt-tracking
//     attribute (the OTel-path equivalent of ddtrace Python's
//     LLMObs.annotate(prompt=...)), so the LLM Obs UI shows which template
//     and version produced the span and whether it came from the registry or
//     the local fallback.
//   - targeting_key / job_role: the targeting inputs that drove this
//     resolution (see AmbientTargetingContext), so a trace shows why a
//     version was served, not just which.
//
// This runs as each span ENDS, not when it starts. MAF has no invoke_agent
// activity of its own: OpenTelemetryAgent renames the "chat" activity that
// Microsoft.Extensions.AI's OpenTelemetryChatClient already started, then
// sets gen_ai.operation.name=invoke_agent and gen_ai.agent.id on it
// (ilspycmd, Microsoft.Agents.AI 1.20.0: UpdateCurrentActivity). At start,
// neither the operation nor the agent id is known yet, which is why an
// ActivityStarted listener never tagged these spans. Registered ahead of the
// OTLP exporter, OnEnd runs before the exporter sees the activity.
//
// gen_ai.agent.id is the id AgentHolder set as ChatClientAgentOptions.Id
// ("router", "specialist-engineering", ...), so it doubles as a key into
// SpecialistRegistry.PromptHolders, passed in as promptHolders. A chat span
// nests inside its invoke_agent parent, so it reads the id off Activity.Parent.
public sealed class PromptTrackingProcessor(IReadOnlyDictionary<string, PromptHolder> promptHolders) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        var agentId = (activity.GetTagItem("gen_ai.operation.name") as string) switch
        {
            "invoke_agent" => activity.GetTagItem("gen_ai.agent.id") as string,
            "chat" => activity.Parent?.GetTagItem("gen_ai.agent.id") as string,
            _ => null,
        };
        if (agentId is null || !promptHolders.TryGetValue(agentId, out var holder)) return;

        var current = holder.Current;
        var promptVersion = current.Source is "registry" or "flag-pinned"
            ? $"{current.Source}-{current.Version}"
            : "v1-" + ShortContentHash(current.Template);
        activity.SetTag("prompt.version", promptVersion);
        activity.SetTag("_dd.ml_obs.prompt_tracking", JsonSerializer.Serialize(new
        {
            id = holder.PromptId,
            version = promptVersion,
            template = current.Template,
            source = current.Source,
        }));

        if (AmbientTargetingContext.TargetingKey is { } targetingKey)
            activity.SetTag("targeting_key", targetingKey);
        if (AmbientTargetingContext.JobRole is { } jobRole)
            activity.SetTag("job_role", jobRole);
    }

    private static string ShortContentHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant();
    }
}
