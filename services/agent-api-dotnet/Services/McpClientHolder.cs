using System.Diagnostics;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace InfraAdvisor.AgentApi.Services;

// Manages the lifecycle of the MCP client + its tool list with on-demand
// reconnect.
//
// Why this exists: ModelContextProtocol.AspNetCore 1.3.0's HTTP transport
// is session-stateful. The server-side session is invalidated whenever
// mcp-server-dotnet restarts (rollout, OOM, AKS rebalance, image pull).
// After that, calls can fail with an expired session or an unexpected
// cancellation from the closed transport, even with a live caller token.
//
// Previously this required manually `kubectl rollout restart deployment/
// agent-api-dotnet` every time mcp-server rolled. With this holder we
// recover inside tool invocation, before FunctionInvokingChatClient turns
// exceptions into tool results, and retry once using the refreshed tools.
//
// Thread-safety: GetClientAsync / GetToolsAsync use a Lazy-style
// double-check. RefreshAsync serializes through a SemaphoreSlim. Tool
// recovery checks the failed tool's identity under that lock so concurrent
// failures from one client coalesce into one reconnect.
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

    public async ValueTask<object?> InvokeToolAsync(
        AIFunction function, AIFunctionArguments arguments, CancellationToken ct)
    {
        // Handoff/local functions also pass through FunctionInvoker. Only
        // MCP tools belong to this holder's session and recovery policy.
        if (function is not McpClientTool)
            return await function.InvokeAsync(arguments, ct);

        ct.ThrowIfCancellationRequested();
        // An in-flight workflow can retain tools from a superseded client.
        // Always resolve by name so subsequent calls use the current session.
        var tool = await GetCurrentToolAsync(function.Name, ct);
        try
        {
            return await tool.InvokeAsync(arguments, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsTransportFailure(ex))
        {
            _logger.LogWarning(ex,
                "[mcp] tool {ToolName} failed with a transport/session error; refreshing and retrying once (gen {Generation})",
                function.Name, Generation);
            Activity.Current?.SetTag("tool.mcp_retried", true);

            await RefreshAsync(tool, ct);
            var retryTool = await GetCurrentToolAsync(function.Name, ct);
            ct.ThrowIfCancellationRequested();
            Activity.Current?.SetTag("tool.mcp_retry_generation", Generation);
            // Outside the original try: a second failure must escape rather
            // than reconnecting or replaying this tool indefinitely.
            return await retryTool.InvokeAsync(arguments, ct);
        }
    }

    private async Task<McpClientTool> GetCurrentToolAsync(string name, CancellationToken ct)
    {
        var tools = await GetToolsAsync(ct);
        return tools.OfType<McpClientTool>().FirstOrDefault(t => t.Name == name)
            ?? throw new InvalidOperationException($"MCP tool '{name}' is no longer available in the current session.");
    }

    private static bool IsTransportFailure(Exception ex)
    {
        if (IsSessionExpired(ex)) return true;
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            // Includes TaskCanceledException. HttpClient timeouts can also
            // take this path, so recovery is bounded to one attempt.
            if (e is OperationCanceledException) return true;
        }
        return false;
    }

    internal static bool IsSessionExpired(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is ClientTransportClosedException) return true;
            if (e is McpException && e.Message.Contains("session", StringComparison.OrdinalIgnoreCase))
                return true;
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } &&
                e.Message.Contains("/mcp", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
    // from inside an exception handler. Tool recovery supplies the failed
    // tool so another caller's successful refresh can satisfy this one.
    public Task RefreshAsync(CancellationToken ct) => RefreshAsync(null, ct);

    private async Task RefreshAsync(McpClientTool? failedTool, CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (failedTool is not null && !_tools.Any(t => ReferenceEquals(t, failedTool)))
                return;
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
