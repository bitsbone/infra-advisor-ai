using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace InfraAdvisor.AgentApi.Services;

// Manages the lifecycle of the MCP client + its tool list with on-demand
// reconnect.
//
// Why this exists: ModelContextProtocol.AspNetCore 1.3.0's HTTP transport
// is session-stateful. The server-side session is invalidated whenever
// mcp-server-dotnet restarts (rollout, OOM, AKS rebalance, image pull).
// After that, every tool call on the cached McpClient returns HTTP 404
// with "session expired" and the agent answer degrades.
//
// Previously this required manually `kubectl rollout restart deployment/
// agent-api-dotnet` every time mcp-server rolled. With this holder we
// catch the expired-session error at call time, dispose + recreate the
// client + tool list, and let the caller retry once transparently.
//
// Thread-safety: GetClientAsync / GetToolsAsync use a Lazy-style
// double-check. RefreshAsync serializes through a SemaphoreSlim so
// concurrent in-flight tool calls coalesce into one reconnect.
public class McpClientHolder : IAsyncDisposable
{
    private readonly string _serverUrl;
    private readonly string _clientName;
    private readonly ILogger<McpClientHolder> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private McpClient? _client;
    private IList<AITool> _tools = Array.Empty<AITool>();
    private long _generation;  // bumped on every successful (re)connect

    public McpClientHolder(
        string serverUrl,
        string clientName,
        ILogger<McpClientHolder> logger,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _serverUrl = serverUrl;
        _clientName = clientName;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    // Generation increments on each successful (re)connect — callers (eg
    // AgentHolder) cache the agent against the generation and rebuild
    // when it changes.
    public long Generation => Interlocked.Read(ref _generation);

    public async Task<McpClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;
        await EnsureConnectedAsync(ct);
        return _client!;
    }

    public async Task<IList<AITool>> GetToolsAsync(CancellationToken ct)
    {
        if (_client is not null) return _tools;
        await EnsureConnectedAsync(ct);
        return _tools;
    }

    // How long to keep a superseded McpClient alive after a refresh before
    // disposing it, so an unrelated request's already-in-flight tool call
    // (bound to the old client, captured before the refresh happened) has
    // time to finish naturally instead of being cancelled out from under it.
    // See ConnectNoLockAsync's comment for why disposing immediately is
    // actively dangerous, not just wasteful.
    private static readonly TimeSpan _staleClientDisposeDelay = TimeSpan.FromSeconds(60);

    // Force a reconnect — connects a fresh client (old one is disposed later,
    // see ConnectNoLockAsync) and re-runs the init handshake. Safe to call
    // from inside an exception handler; concurrent callers will see the same
    // new client after one round trip thanks to the connect lock.
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            await ConnectNoLockAsync(ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_client is not null) return;
            await ConnectNoLockAsync(ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ConnectNoLockAsync(CancellationToken ct)
    {
        // `ct` here belongs to whichever request happened to trigger this
        // (re)connect, but the resulting McpClient is cached and shared by
        // every subsequent request until the next RefreshAsync. It must only
        // gate the handshake below (CreateAsync/ListToolsAsync) — it must
        // never be captured by the transport for later use, or an unrelated
        // request's cancellation (e.g. the browser navigating away mid-SSE)
        // could tear down the shared client for everyone else. Diagnostic
        // log to confirm/refute this as the source of the near-instant
        // TaskCanceledException seen recurring in Datadog Error Tracking
        // (issue 1d5322a6-5065-11f1-91de-da7ad0900003) on MCP tool calls.
        _logger.LogDebug(
            "[mcp] connecting to {Url}; caller ct already cancelled: {AlreadyCancelled}",
            _serverUrl, ct.IsCancellationRequested);

        // Use an IHttpClientFactory-managed HttpClient so OTel's
        // AddHttpClientInstrumentation() delegating handler is in the pipeline,
        // making MCP HTTP calls visible as child spans in Datadog APM.
        var httpClient = _httpClientFactory.CreateClient("mcp-dotnet");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_serverUrl), Name = _clientName },
            httpClient,
            _loggerFactory,
            ownsHttpClient: false);
        var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var listed = await client.ListToolsAsync(cancellationToken: ct);

        // Swap in the new client/tools before touching the old one. A caller
        // that already captured the OLD _tools (e.g. mid-tool-call, from
        // before this refresh started) is holding a direct reference to the
        // old McpClient — disposing it immediately would cancel that
        // in-flight call out from under an otherwise-healthy, unrelated
        // request: ModelContextProtocol.Core's StreamableHttpClientSessionTransport
        // links every outgoing request's CancellationToken to one
        // transport-instance-wide CTS that only fires on DisposeAsync(), and
        // that CTS is shared by every concurrent call using this client —
        // confirmed root cause of a recurring near-instant TaskCanceledException
        // on execute_tool (Datadog Error Tracking issue
        // 1d5322a6-5065-11f1-91de-da7ad0900003; ilspycmd-verified against the
        // installed ModelContextProtocol.Core 1.3.0 package). So: dispose the
        // superseded client only after a grace period, not synchronously.
        var previous = _client;
        _client = client;
        _tools = [.. listed];
        Interlocked.Increment(ref _generation);
        _logger.LogInformation(
            "[mcp] connected to {Url} (gen {Generation}); loaded {Count} tool(s): {Tools}",
            _serverUrl, _generation, _tools.Count,
            string.Join(", ", listed.Select(t => t.Name)));

        if (previous is not null)
        {
            ScheduleDelayedDispose(previous);
        }
    }

    private void ScheduleDelayedDispose(McpClient previous)
    {
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(_staleClientDisposeDelay); }
            catch (Exception ex)
            {
                _logger.LogDebug("Delay before disposing stale MCP client was interrupted: {Error}", ex.Message);
            }
            try { await previous.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogDebug("Ignoring error disposing stale MCP client after grace period: {Error}", ex.Message);
            }
        });
    }

    private async Task DisposeClientNoLockAsync()
    {
        var old = _client;
        _client = null;
        _tools = Array.Empty<AITool>();
        if (old is null) return;
        try { await old.DisposeAsync(); }
        catch (Exception ex)
        {
            _logger.LogDebug("Ignoring error disposing stale MCP client: {Error}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connectLock.WaitAsync();
        try { await DisposeClientNoLockAsync(); }
        finally { _connectLock.Release(); }
        _connectLock.Dispose();
    }
}
