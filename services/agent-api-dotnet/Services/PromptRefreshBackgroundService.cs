namespace InfraAdvisor.AgentApi.Services;

// Periodically re-resolves PromptHolder's prompt-version flag + registry
// fetch, so a version bump in the Datadog UI reaches this pod without a
// redeploy. One failed iteration never stops the loop — PromptHolder's own
// fail-open fetch already guarantees a usable prompt either way.
public class PromptRefreshBackgroundService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly PromptHolder _holder;
    private readonly ILogger<PromptRefreshBackgroundService> _logger;

    public PromptRefreshBackgroundService(PromptHolder holder, ILogger<PromptRefreshBackgroundService> logger)
    {
        _holder = holder;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm the first resolution eagerly so the very first /query doesn't
        // pay the fetch latency inline — mirrors the old startup-once fetch's
        // behavior without blocking app startup on it.
        try
        {
            await _holder.RefreshAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[prompt] initial refresh failed — using fallback until the next periodic retry.");
        }

        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _holder.RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[prompt] periodic refresh failed — keeping the previously resolved prompt.");
            }
        }
    }
}
