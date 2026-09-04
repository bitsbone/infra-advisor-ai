using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using InfraAdvisor.AgentApi.Observability;

namespace InfraAdvisor.AgentApi.Services;

// Holds the current ChatClientAgent instance + rebuilds it when the
// underlying MCP tool list changes (after an McpClientHolder.RefreshAsync)
// or the effective system prompt changes (after a PromptHolder refresh —
// see PromptRefreshBackgroundService).
//
// Why a holder instead of a plain DI singleton: ChatClientAgent's
// ChatOptions.Tools/Instructions are captured at construction. To pick up
// a refreshed tool list or prompt version we must rebuild the agent.
// Tracking both holders' Generation lets us rebuild lazily — once per
// change — rather than per request.
public class AgentHolder
{
    private readonly IChatClient _chatClient;
    private readonly McpClientHolder _mcpHolder;
    private readonly PromptHolder _promptHolder;
    private readonly string _agentName;
    private readonly string _otelSourceName;
    private readonly object _lock = new();

    private AIAgent? _agent;
    private long _builtForMcpGeneration = -1;
    private long _builtForPromptGeneration = -1;

    public AgentHolder(
        IChatClient chatClient,
        McpClientHolder mcpHolder,
        PromptHolder promptHolder,
        string agentName,
        string otelSourceName)
    {
        _chatClient = chatClient;
        _mcpHolder = mcpHolder;
        _promptHolder = promptHolder;
        _agentName = agentName;
        _otelSourceName = otelSourceName;
    }

    public async Task<AIAgent> GetAgentAsync(CancellationToken ct)
    {
        var tools = await _mcpHolder.GetToolsAsync(ct);
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
        var fresh = new ChatClientAgent(
                _chatClient,
                new ChatClientAgentOptions
                {
                    Name = _agentName,
                    ChatOptions = new ChatOptions
                    {
                        Instructions = prompt.Template,
                        Tools = tools,
                    },
                })
            .AsBuilder()
            .UseOpenTelemetry(sourceName: _otelSourceName,
                              configure: cfg => cfg.EnableSensitiveData = TelemetryPrivacy.EnableSensitiveData)
            .Build();

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
}
