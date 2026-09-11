namespace InfraAdvisor.AgentApi.Observability;

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
    // Instance field, not a shared const — one PromptHolder now exists per
    // managed prompt (router + 5 specialists, see SpecialistRegistry), each
    // reading its own prompt_id from the same Datadog Prompt Registry
    // Python's seed script already populated.
    public string PromptId { get; }

    private readonly DatadogPromptManagementClient _client;
    private readonly PromptVersionFlags _flags;
    private readonly string _fallback;
    private readonly ILogger<PromptHolder> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile PromptFetchResult? _current;
    private long _generation;

    public PromptHolder(
        string promptId,
        DatadogPromptManagementClient client,
        PromptVersionFlags flags,
        string fallback,
        ILogger<PromptHolder> logger)
    {
        PromptId = promptId;
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
            // Pod-wide/default resolution — no per-user targeting context
            // (see ResolveForRequestAsync for the per-request, per-user
            // targeted variant). Resolution order: (1) the registry's own
            // __llmobs__.prompt.<prompt_id> Feature Flag, (2) the REST
            // registry fetch (latest version), (3) the hardcoded fallback —
            // matches agent-api's observability/prompts.py exactly.
            var fetched = await _flags.ResolveAsync(PromptId, targetingKey: null, attributes: null, ct)
                ?? await _client.GetPromptTemplateAsync(PromptId, _fallback, ct);

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

    // Per-request, per-user targeted resolution — evaluates the Feature
    // Flag fresh with the calling user's targeting_key/attributes (e.g. the
    // demo job_role attribute) rather than using the pod-wide cached
    // Current. Returns Current unchanged when there's no targeting context
    // to evaluate, or when the flag doesn't resolve differently for this
    // user (no targeting rule matched, or it matched the same version the
    // pod-wide default already has) — the common case, kept cheap and
    // cache-hitting. Only a genuine per-user override triggers the caller
    // (AgentHolder.GetAgentForRequestAsync) to build an ad-hoc agent for
    // just this turn.
    public async Task<PromptFetchResult> ResolveForRequestAsync(
        string? targetingKey, IReadOnlyDictionary<string, string>? attributes, CancellationToken ct)
    {
        if (targetingKey is null && (attributes is null || attributes.Count == 0))
            return Current;

        var resolved = await _flags.ResolveAsync(PromptId, targetingKey, attributes, ct);
        return resolved ?? Current;
    }
}
