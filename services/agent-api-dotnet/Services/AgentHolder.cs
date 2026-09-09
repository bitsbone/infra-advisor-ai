using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using InfraAdvisor.AgentApi.Observability;

namespace InfraAdvisor.AgentApi.Services;

// Holds the current AIAgent instance for ONE specialist (or the router) +
// rebuilds it when the underlying MCP tool list changes (after an
// McpClientHolder.RefreshAsync) or the effective system prompt changes
// (after a PromptHolder refresh — see PromptRefreshBackgroundService).
//
// Why a holder instead of a plain DI singleton: ChatClientAgent's
// ChatOptions.Tools/Instructions are captured at construction. To pick up
// a refreshed tool list or prompt version we must rebuild the agent.
// Tracking both holders' Generation lets us rebuild lazily — once per
// change — rather than per request.
//
// allowedToolNames mirrors Python's _TOOL_PARTITIONS (services/agent-api/
// src/agent.py) — null means "all tools" (the router, and Python's
// "general" specialist), non-null filters McpClientHolder's full tool
// list down to just the names in the set.
public class AgentHolder
{
    private readonly IChatClient _chatClient;
    private readonly McpClientHolder _mcpHolder;
    private readonly PromptHolder _promptHolder;
    private readonly string _agentName;
    private readonly string _otelSourceName;
    private readonly IReadOnlySet<string>? _allowedToolNames;
    private readonly object _lock = new();

    private AIAgent? _agent;
    private long _builtForMcpGeneration = -1;
    private long _builtForPromptGeneration = -1;

    public AgentHolder(
        IChatClient chatClient,
        McpClientHolder mcpHolder,
        PromptHolder promptHolder,
        string agentName,
        string otelSourceName,
        IReadOnlySet<string>? allowedToolNames = null)
    {
        _chatClient = chatClient;
        _mcpHolder = mcpHolder;
        _promptHolder = promptHolder;
        _agentName = agentName;
        _otelSourceName = otelSourceName;
        _allowedToolNames = allowedToolNames;
    }

    public async Task<AIAgent> GetAgentAsync(CancellationToken ct)
    {
        var tools = await GetToolsAsync(ct);
        var mcpGen = _mcpHolder.Generation;
        var prompt = await _promptHolder.GetOrRefreshAsync(ct);
        var promptGen = _promptHolder.Generation;

        lock (_lock)
        {
            if (_agent is not null && _builtForMcpGeneration == mcpGen && _builtForPromptGeneration == promptGen)
                return _agent;
        }

        // Build the new agent outside the lock — UseOpenTelemetry chain
        // is cheap but not zero, and we don't want to block sibling
        // requests during construction.
        var fresh = BuildAgent(prompt.Template, tools);

        lock (_lock)
        {
            // Another concurrent caller may have built the same generation
            // already — prefer theirs to avoid orphaning an agent we're
            // about to replace.
            if (_agent is not null && _builtForMcpGeneration == mcpGen && _builtForPromptGeneration == promptGen)
                return _agent;
            _agent = fresh;
            _builtForMcpGeneration = mcpGen;
            _builtForPromptGeneration = promptGen;
            return _agent;
        }
    }

    // Per-request, per-user targeted variant (see PromptHolder.
    // ResolveForRequestAsync) — evaluates this specialist's prompt flag
    // fresh with the calling user's targeting context. Returns the cached
    // default agent (no extra construction) unless the resolved template
    // actually differs from the pod-wide default, in which case it builds
    // a one-off agent for just this turn — not cached, not tracked by
    // Generation, since it's specific to one user's targeting context.
    public async Task<(AIAgent Agent, bool IsOverride)> GetAgentForRequestAsync(
        string? targetingKey, IReadOnlyDictionary<string, string>? attributes, CancellationToken ct)
    {
        var cached = await GetAgentAsync(ct); // ensures the pod-wide default is built + Current is fresh
        var resolved = await _promptHolder.ResolveForRequestAsync(targetingKey, attributes, ct);
        if (resolved.Template == _promptHolder.Current.Template)
            return (cached, false);

        var tools = await GetToolsAsync(ct);
        return (BuildAgent(resolved.Template, tools), true);
    }

    private async Task<IList<AITool>> GetToolsAsync(CancellationToken ct)
    {
        var allTools = await _mcpHolder.GetToolsAsync(ct);
        return _allowedToolNames is null
            ? allTools
            : allTools.Where(t => _allowedToolNames.Contains(t.Name)).ToList();
    }

    private AIAgent BuildAgent(string template, IList<AITool> tools) =>
        new ChatClientAgent(
                _chatClient,
                new ChatClientAgentOptions
                {
                    Id = _agentName,
                    Name = _agentName,
                    ChatOptions = new ChatOptions
                    {
                        Instructions = template,
                        Tools = tools,
                    },
                })
            .AsBuilder()
            .UseOpenTelemetry(sourceName: _otelSourceName,
                              configure: cfg => cfg.EnableSensitiveData = TelemetryPrivacy.EnableSensitiveData)
            .Build();
}
