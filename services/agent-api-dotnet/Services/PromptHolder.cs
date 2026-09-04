namespace InfraAdvisor.AgentApi.Services;

// Holds the currently effective system prompt + refreshes it periodically
// from Datadog's Prompt Registry (via DatadogPromptManagementClient),
// honoring any Feature Flags-pinned version (via PromptVersionFlags).
//
// Why not the old startup-once fetch: a version bump in the Datadog UI
// should reach a running pod without a redeploy — the entire point of
// prompt management/targeting. Mirrors McpClientHolder's lazy-connect +
// Generation-tracking shape, but much simpler: no connection/session
// lifecycle, just re-resolve-and-refetch on a timer
// (PromptRefreshBackgroundService) plus lazily on first use.
public class PromptHolder
{
    public const string PromptId = "infra-advisor-system-prompt";

    private readonly DatadogPromptManagementClient _client;
    private readonly PromptVersionFlags _flags;
    private readonly string _fallback;
    private readonly ILogger<PromptHolder> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile PromptFetchResult? _current;
    private long _generation;

    public PromptHolder(
        DatadogPromptManagementClient client,
        PromptVersionFlags flags,
        string fallback,
        ILogger<PromptHolder> logger)
    {
        _client = client;
        _flags = flags;
        _fallback = fallback;
        _logger = logger;
    }

    // Bumped every time a refresh changes the effective template/version —
    // AgentHolder keys its rebuild-check on this alongside McpClientHolder.Generation.
    public long Generation => Interlocked.Read(ref _generation);

    public PromptFetchResult Current => _current ?? new PromptFetchResult(_fallback, "fallback", "fallback");

    public async Task<PromptFetchResult> GetOrRefreshAsync(CancellationToken ct)
    {
        if (_current is not null) return _current;
        await RefreshAsync(ct);
        return Current;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var version = await _flags.ResolveVersionAsync(PromptId, ct);
            var fetched = await _client.GetPromptTemplateAsync(PromptId, _fallback, version, ct);

            var prev = _current;
            if (prev is null || prev.Template != fetched.Template || prev.Version != fetched.Version)
            {
                _current = fetched;
                Interlocked.Increment(ref _generation);
                _logger.LogInformation(
                    "[prompt] {PromptId} resolved: version={Version} source={Source}",
                    PromptId, fetched.Version, fetched.Source);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
