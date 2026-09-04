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
// Fails open on any error (provider registration or flag evaluation):
// a Feature Flags outage must never block a prompt from resolving via
// DatadogPromptManagementClient's own fallback.
public class PromptVersionFlags
{
    private readonly ILogger<PromptVersionFlags> _logger;
    private static readonly SemaphoreSlim _initLock = new(1, 1);
    private static bool _providerSet;

    public PromptVersionFlags(ILogger<PromptVersionFlags> logger)
    {
        _logger = logger;
    }

    private async Task EnsureProviderAsync()
    {
        if (_providerSet) return;
        await _initLock.WaitAsync();
        try
        {
            if (_providerSet) return;
            await Api.Instance.SetProviderAsync(new DatadogProvider());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Datadog OpenFeature provider — prompt-version flags will use defaults (0).");
        }
        finally
        {
            _providerSet = true; // set even on failure — avoids retrying registration on every call
            _initLock.Release();
        }
    }

    public async Task<int> ResolveVersionAsync(string promptId, CancellationToken ct = default)
    {
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
