using Datadog.FeatureFlags.OpenFeature;
using OpenFeature;
using OpenFeature.Model;

namespace InfraAdvisor.AgentApi.Observability;

// Evaluates the Datadog Prompt Registry's own auto-provisioned
// `__llmobs__.prompt.<prompt_id>` Feature Flag — one per managed prompt,
// created the moment that prompt exists in the registry, no manual flag
// setup required. Mirrors agent-api's observability/prompts.py (which lets
// ddtrace's own LLMObs.get_prompt() evaluate the same flag internally) so
// both backends read the exact same flag/targeting rules — see
// docs/src/content/docs/llm-engineering/monitoring/prompt-targeting.mdx.
//
// This replaced an earlier version of this file that evaluated a custom
// integer flag (`prompt-version.<prompt_id>`) hand-rolled specifically for
// this app. That flag is gone: the registry's own flag is real, documented,
// and (per Datadog's own server-flag-evaluation-metrics guide) monitored —
// no reason to maintain a parallel mechanism.
//
// Disabled gracefully when DD_PROMPT_MANAGEMENT_ENABLED isn't "true" — this
// must have zero footprint (no OpenFeature provider registration, no network
// activity) when the feature is off, exactly like DatadogPromptManagementClient's
// own _enabled gate. A prior version of this file registered the provider
// unconditionally, which meant enabling prompt management wasn't actually
// required to reach Datadog Feature Flags' provider-initialization path —
// this caused a production incident (see git history) where startup hung
// on OpenFeature provider init even with DD_PROMPT_MANAGEMENT_ENABLED=false.
//
// Fails open on any error or timeout (provider registration or flag
// evaluation): a Feature Flags outage must never block a prompt from
// resolving via DatadogPromptManagementClient's own fallback, and must
// never block application startup.
public class PromptVersionFlags
{
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(3);

    private readonly bool _enabled;
    private readonly ILogger<PromptVersionFlags> _logger;
    private static readonly SemaphoreSlim _initLock = new(1, 1);
    private static bool _providerSet;

    public PromptVersionFlags(ILogger<PromptVersionFlags> logger)
    {
        _logger = logger;
        _enabled = string.Equals(
            Environment.GetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureProviderAsync()
    {
        if (_providerSet) return;
        await _initLock.WaitAsync();
        try
        {
            if (_providerSet) return;
            var registered = Api.Instance.SetProviderAsync(new DatadogProvider());
            var completed = await Task.WhenAny(registered, Task.Delay(InitTimeout));
            if (completed != registered)
            {
                _logger.LogWarning(
                    "Datadog OpenFeature provider registration did not complete within {Timeout} — prompt flag evaluation will fall through to the registry HTTP fetch until it does.",
                    InitTimeout);
            }
            else
            {
                await registered; // observe any exception now that it has completed
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Datadog OpenFeature provider — prompt flag evaluation will fall through to the registry HTTP fetch.");
        }
        finally
        {
            _providerSet = true; // set even on failure/timeout — avoids retrying registration on every call
            _initLock.Release();
        }
    }

    // Evaluates __llmobs__.prompt.<promptId> as an object value (ilspycmd-
    // confirmed against the installed OpenFeature 2.3.0 package:
    // FeatureClient.GetObjectValueAsync(flagKey, Value defaultValue,
    // EvaluationContext?, ...) -> Value, backed by OpenFeature.Model.Structure)
    // — the same object shape ddtrace's Python _fetch_from_ff parses
    // (template + user_version/version). Returns null on any non-hit
    // (disabled, provider not ready, empty structure, error) so the caller
    // falls through to DatadogPromptManagementClient's REST fetch, exactly
    // like Python's manager.py falls through to its HTTP /resolve floor.
    //
    // targetingKey/attributes let a targeting rule on the flag resolve a
    // different version per user (e.g. keyed on the demo job_role
    // attribute) — both are optional; a pod-wide/default resolution (no
    // per-user targeting) passes null/empty for both.
    public async Task<PromptFetchResult?> ResolveAsync(
        string promptId,
        string? targetingKey,
        IReadOnlyDictionary<string, string>? attributes,
        CancellationToken ct = default)
    {
        if (!_enabled) return null;

        try
        {
            await EnsureProviderAsync();
            var client = Api.Instance.GetClient();

            var contextBuilder = EvaluationContext.Builder();
            if (targetingKey is not null) contextBuilder.SetTargetingKey(targetingKey);
            if (attributes is not null)
                foreach (var (attrKey, attrValue) in attributes)
                    contextBuilder.Set(attrKey, attrValue);
            var context = contextBuilder.Build();

            var value = await client.GetObjectValueAsync(
                $"__llmobs__.prompt.{promptId}", new Value(Structure.Empty), context, cancellationToken: ct);
            var structure = value.AsStructure;
            if (structure is null || structure.Count == 0) return null;

            var template = structure.TryGetValue("template", out var templateValue) ? templateValue?.AsString : null;
            if (string.IsNullOrEmpty(template)) return null;

            var version =
                (structure.TryGetValue("user_version", out var uv) ? uv?.AsString : null)
                ?? (structure.TryGetValue("version", out var v) ? (v?.AsString ?? v?.AsInteger?.ToString()) : null)
                ?? "unknown";

            return new PromptFetchResult(template, version, "ff");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prompt flag evaluation failed for {PromptId} — falling through to registry fetch.", promptId);
            return null;
        }
    }
}
