using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using InfraAdvisor.AgentApi.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class McpClientHolderTests
{
    [Theory]
    [InlineData("get_contract_awards")]
    [InlineData("get_procurement_opportunities")]
    public async Task Unexpected_cancellation_reconnects_and_retries_with_fresh_tool_and_arguments(string name)
    {
        using var http = new McpHttpFactory(generation => new McpHandler(generation)
        {
            ToolCallAsync = generation == 1
                ? (_, _) => throw new TaskCanceledException("A task was canceled.")
                : (_, _) => Task.FromResult("fresh-client-result"),
        });
        await using var holder = CreateHolder(http);
        var tool = await GetToolAsync(holder, name);
        using var caller = new CancellationTokenSource();

        var result = await holder.InvokeToolAsync(tool, Arguments(), caller.Token);

        Assert.False(caller.IsCancellationRequested);
        Assert.Contains("fresh-client-result", JsonSerializer.Serialize(result));
        Assert.Equal(2, holder.Generation);
        Assert.Equal(2, http.Count);
        Assert.Equal(1, http.Handlers[1].CallCount);
        Assert.Equal(1, http.Handlers[2].CallCount);
        Assert.Equal(name, http.Handlers[2].LastToolName);
        Assert.Equal("CA", http.Handlers[2].LastArguments.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("operation_cancelled")]
    [InlineData("wrapped_cancellation")]
    [InlineData("session_expired")]
    [InlineData("http_404")]
    public async Task Other_transport_signals_also_recover_once(string failure)
    {
        using var http = new McpHttpFactory(generation => new McpHandler(generation)
        {
            ToolCallAsync = generation == 1
                ? (_, _) => throw TransportFailure(failure)
                : (_, _) => Task.FromResult("recovered"),
        });
        await using var holder = CreateHolder(http);
        var tool = await GetToolAsync(holder);

        var result = await holder.InvokeToolAsync(tool, Arguments(), CancellationToken.None);

        Assert.Contains("recovered", JsonSerializer.Serialize(result));
        Assert.Equal(2, http.Count);
        Assert.Equal(1, http.Handlers[2].CallCount);
    }

    [Fact]
    public async Task Already_cancelled_caller_does_not_invoke_or_reconnect()
    {
        using var http = new McpHttpFactory();
        await using var holder = CreateHolder(http);
        var tool = await GetToolAsync(holder);
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            holder.InvokeToolAsync(tool, Arguments(), caller.Token).AsTask());

        Assert.Equal(1, http.Count);
        Assert.Equal(0, http.Handlers[1].CallCount);
    }

    [Theory]
    [InlineData("operation_cancelled")]
    [InlineData("session_expired")]
    public async Task Caller_cancellation_during_call_does_not_reconnect(string failure)
    {
        using var caller = new CancellationTokenSource();
        using var http = new McpHttpFactory(generation => new McpHandler(generation)
        {
            ToolCallAsync = (_, _) =>
            {
                caller.Cancel();
                throw TransportFailure(failure);
            },
        });
        await using var holder = CreateHolder(http);
        var tool = await GetToolAsync(holder);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            holder.InvokeToolAsync(tool, Arguments(), caller.Token).AsTask());

        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(1, http.Count);
        Assert.Equal(1, http.Handlers[1].CallCount);
    }

    [Fact]
    public async Task Second_transport_failure_escapes_without_a_third_attempt()
    {
        using var http = new McpHttpFactory(generation => new McpHandler(generation)
        {
            ToolCallAsync = (_, _) => throw new TaskCanceledException("A task was canceled."),
        });
        await using var holder = CreateHolder(http);
        var tool = await GetToolAsync(holder);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            holder.InvokeToolAsync(tool, Arguments(), CancellationToken.None).AsTask());

        Assert.Equal(2, http.Count);
        Assert.Equal(1, http.Handlers[1].CallCount);
        Assert.Equal(1, http.Handlers[2].CallCount);
    }

    [Fact]
    public async Task Ordinary_tool_failure_does_not_reconnect()
    {
        using var http = new McpHttpFactory(generation => new McpHandler(generation)
        {
            ToolCallAsync = (_, _) => throw new McpException("Invalid tool arguments"),
        });
        await using var holder = CreateHolder(http);
        var tool = await GetToolAsync(holder);

        await Assert.ThrowsAsync<McpException>(() =>
            holder.InvokeToolAsync(tool, Arguments(), CancellationToken.None).AsTask());

        Assert.Equal(1, http.Count);
        Assert.Equal(1, http.Handlers[1].CallCount);
    }

    [Fact]
    public async Task Local_function_cancellation_does_not_create_an_mcp_client()
    {
        using var http = new McpHttpFactory();
        await using var holder = CreateHolder(http);
        var calls = 0;
        var local = AIFunctionFactory.Create((Func<string>)(() =>
        {
            calls++;
            throw new TaskCanceledException("Local cancellation");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            holder.InvokeToolAsync(local, new AIFunctionArguments(), CancellationToken.None).AsTask());

        Assert.Equal(1, calls);
        Assert.Equal(0, http.Count);
    }

    [Fact]
    public async Task Workflow_with_a_stale_tool_uses_current_client_without_another_refresh()
    {
        using var http = new McpHttpFactory();
        await using var holder = CreateHolder(http);
        var staleTool = await GetToolAsync(holder);
        await holder.RefreshAsync(CancellationToken.None);

        var result = await holder.InvokeToolAsync(staleTool, Arguments(), CancellationToken.None);

        Assert.Contains("client-2", JsonSerializer.Serialize(result));
        Assert.Equal(2, http.Count);
        Assert.Equal(0, http.Handlers[1].CallCount);
        Assert.Equal(1, http.Handlers[2].CallCount);
    }

    [Fact]
    public async Task Tool_removed_after_refresh_does_not_fall_back_to_failed_tool()
    {
        using var http = new McpHttpFactory(generation => new McpHandler(generation)
        {
            ToolNames = generation == 1 ? ["get_contract_awards"] : [],
            ToolCallAsync = (_, _) => throw new TaskCanceledException("A task was canceled."),
        });
        await using var holder = CreateHolder(http);
        var tool = await GetToolAsync(holder);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            holder.InvokeToolAsync(tool, Arguments(), CancellationToken.None).AsTask());

        Assert.Contains("get_contract_awards", error.Message);
        Assert.Equal(2, http.Count);
        Assert.Equal(1, http.Handlers[1].CallCount);
        Assert.Equal(0, http.Handlers[2].CallCount);
    }

    [Fact]
    public async Task Concurrent_failures_share_one_refresh_and_retry_on_the_same_client()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        using var http = new McpHttpFactory(generation => new McpHandler(generation)
        {
            ToolCallAsync = generation == 1 ? async (_, ct) =>
            {
                if (Interlocked.Increment(ref arrivals) == 2) bothStarted.SetResult();
                await bothStarted.Task.WaitAsync(ct);
                throw new TaskCanceledException("A task was canceled.");
            } : (_, _) => Task.FromResult("recovered-together"),
        });
        await using var holder = CreateHolder(http);
        var tool = await GetToolAsync(holder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var results = await Task.WhenAll(
            holder.InvokeToolAsync(tool, Arguments(), timeout.Token).AsTask(),
            holder.InvokeToolAsync(tool, Arguments(), timeout.Token).AsTask());

        Assert.All(results, result => Assert.Contains("recovered-together", JsonSerializer.Serialize(result)));
        Assert.Equal(2, http.Count);
        Assert.Equal(2, holder.Generation);
        Assert.Equal(2, http.Handlers[1].CallCount);
        Assert.Equal(2, http.Handlers[2].CallCount);
    }

    private static Exception TransportFailure(string failure) => failure switch
    {
        "operation_cancelled" => new OperationCanceledException("Transport cancelled"),
        "wrapped_cancellation" => new InvalidOperationException("Transport failed", new TaskCanceledException()),
        "session_expired" => new McpException("MCP session has expired"),
        "http_404" => new HttpRequestException("POST /mcp returned 404", null, HttpStatusCode.NotFound),
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    private static McpClientHolder CreateHolder(IHttpClientFactory http) => new(
        "http://mcp.test/mcp", "recovery-tests", NullLogger<McpClientHolder>.Instance,
        http, NullLoggerFactory.Instance);

    private static async Task<McpClientTool> GetToolAsync(
        McpClientHolder holder, string name = "get_contract_awards") =>
        (await holder.GetToolsAsync(CancellationToken.None)).OfType<McpClientTool>().Single(t => t.Name == name);

    private static AIFunctionArguments Arguments() => new() { ["state"] = "CA" };

    // Exercise the real MCP SDK handshake, tool listing, and AIFunction
    // invocation without sockets or an external MCP server. Each factory
    // client represents a distinct transport; only the first one is broken.
    private sealed class McpHttpFactory(Func<int, McpHandler>? createHandler = null) : IHttpClientFactory, IDisposable
    {
        private readonly ConcurrentBag<HttpClient> _clients = [];
        private int _count;
        public ConcurrentDictionary<int, McpHandler> Handlers { get; } = new();
        public int Count => Volatile.Read(ref _count);

        public HttpClient CreateClient(string name)
        {
            var generation = Interlocked.Increment(ref _count);
            var handler = createHandler?.Invoke(generation) ?? new McpHandler(generation);
            Handlers[generation] = handler;
            var client = new HttpClient(handler);
            _clients.Add(client);
            return client;
        }

        public void Dispose()
        {
            foreach (var client in _clients) client.Dispose();
        }
    }

    private sealed class McpHandler(int generation) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public string? LastToolName { get; private set; }
        public JsonElement LastArguments { get; private set; }
        public string[] ToolNames { get; init; } = ["get_contract_awards", "get_procurement_opportunities"];
        public Func<JsonElement, CancellationToken, Task<string>> ToolCallAsync { get; init; } =
            (_, _) => Task.FromResult($"client-{generation}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var message = document.RootElement;
            if (!message.TryGetProperty("id", out var id))
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            object result;
            switch (message.GetProperty("method").GetString())
            {
                case "initialize":
                    result = new
                    {
                        protocolVersion = message.GetProperty("params").GetProperty("protocolVersion").GetString(),
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "test-mcp", version = "1.0" },
                    };
                    break;
                case "tools/list":
                    result = new
                    {
                        tools = ToolNames.Select(name => new
                        {
                            name,
                            description = "Test tool",
                            inputSchema = new { type = "object", properties = new { state = new { type = "string" } } },
                        }),
                    };
                    break;
                case "tools/call":
                    Interlocked.Increment(ref _callCount);
                    var parameters = message.GetProperty("params");
                    LastToolName = parameters.GetProperty("name").GetString();
                    LastArguments = parameters.GetProperty("arguments").Clone();
                    var text = await ToolCallAsync(parameters, ct);
                    result = new { content = new[] { new { type = "text", text } } };
                    break;
                default:
                    throw new InvalidOperationException("Unexpected MCP method");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }),
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
