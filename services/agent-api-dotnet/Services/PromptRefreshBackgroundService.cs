namespace InfraAdvisor.AgentApi.Services;

// Periodically re-resolves every specialist's prompt-version flag +
// registry fetch (router + 5 specialists — see SpecialistRegistry), so a
// version bump in the Datadog UI reaches this pod without a redeploy. One
// failed iteration never stops the loop — each PromptHolder's own
// fail-open fetch already guarantees a usable prompt either way.
public class PromptRefreshBackgroundService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly SpecialistRegistry _registry;
    private readonly ILogger<PromptRefreshBackgroundService> _logger;

    public PromptRefreshBackgroundService(SpecialistRegistry registry, ILogger<PromptRefreshBackgroundService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _registry.RefreshAllPromptsAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[prompt] periodic refresh failed — keeping the previously resolved prompts.");
            }
        }
    }
}
