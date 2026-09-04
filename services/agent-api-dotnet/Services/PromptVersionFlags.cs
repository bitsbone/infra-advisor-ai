using Datadog.FeatureFlags.OpenFeature;
using OpenFeature;
using OpenFeature.Model;

namespace InfraAdvisor.AgentApi.Services;

// Resolves a per-prompt pinned registry version via Datadog Feature Flags
// (prompt-version.<prompt_id>, an integer flag; 0 is the "no override"
// sentinel). Mirrors agent-api's observability/feature_flags.py so both
// backends read the exact same flags — see
// docs/src/content/docs/llm-engineering/monitoring/prompt-targeting.mdx.
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
                    "Datadog OpenFeature provider registration did not complete within {Timeout} — prompt-version flags will use defaults (0) until it does.",
                    InitTimeout);
            }
            else
            {
                await registered; // observe any exception now that it has completed
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Datadog OpenFeature provider — prompt-version flags will use defaults (0).");
        }
        finally
        {
            _providerSet = true; // set even on failure/timeout — avoids retrying registration on every call
            _initLock.Release();
        }
    }

    public async Task<int> ResolveVersionAsync(string promptId, CancellationToken ct = default)
    {
        if (!_enabled) return 0;

        try
        {
            await EnsureProviderAsync();
            var client = Api.Instance.GetClient();
            var context = EvaluationContext.Builder()
                .Set("env", Environment.GetEnvironmentVariable("DD_ENV") ?? "")
                .Build();
            return await client.GetIntegerValueAsync($"prompt-version.{promptId}", 0, context, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prompt-version flag evaluation failed for {PromptId} — using 0 (no override).", promptId);
            return 0;
        }
    }
}
