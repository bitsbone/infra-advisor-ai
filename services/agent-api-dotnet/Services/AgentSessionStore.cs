using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace InfraAdvisor.AgentApi.Services;

// Redis-backed conversation history persistence.
//
// GetOrCreateHistoryAsync/SaveHistoryAsync round-trip a plain
// List<ChatMessage> (via AIJsonUtilities.DefaultOptions, the same
// polymorphic-content JSON contract MAF's own AgentSession serialization
// uses internally) rather than a single ChatClientAgent's AgentSession.
//
// Why not AgentSession anymore: AgentSession is tied to ONE AIAgent
// instance. Since Workstream 1 replaced the single merged agent with a
// HandoffWorkflowBuilder workflow spanning 6 agents (router + 5
// specialists — see SpecialistRegistry), there is no longer one agent
// whose AgentSession could represent "the conversation." Handoff
// workflows also have no serialize/deserialize surface of their own for
// cross-request persistence (ilspycmd-confirmed against the installed
// Microsoft.Agents.AI.Workflows 1.20.0 package — HandoffAgentExecutor
// keeps conversation state in an internal, in-memory-only StatefulExecutor
// field). So this store now owns history directly: load the full message
// list, pass it as the workflow's input on every turn (matching Python's
// agent-api, which is stateless per call in exactly the same way), then
// append the turn's messages and save.
//
// Keyed by TenantSessionKey.Create(jwtSub, conversationOrSessionId), never by
// a public client identifier alone. Sessions persist for 24h past the last
// write — same TTL as the model preference in MemoryService.
public class AgentSessionStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<AgentSessionStore> _logger;
    private const string KeyPrefix = "infra-advisor:agent-session";
    private const int TtlSeconds = 86400;

    public AgentSessionStore(IConnectionMultiplexer redis, ILogger<AgentSessionStore> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    internal static string KeyFor(string tenantSessionKey) => $"{KeyPrefix}:{tenantSessionKey}";

    public async Task<List<ChatMessage>> GetOrCreateHistoryAsync(string conversationId, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            var json = await db.StringGetAsync(KeyFor(conversationId));
            if (!json.IsNullOrEmpty)
            {
                var restored = JsonSerializer.Deserialize<List<ChatMessage>>(
                    (string)json!, AIJsonUtilities.DefaultOptions);
                if (restored is not null) return restored;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to restore agent conversation history; starting fresh error_type={ErrorType}",
                ex.GetType().Name);
        }
        return new List<ChatMessage>();
    }

    public async Task SaveHistoryAsync(string conversationId, List<ChatMessage> history, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(history, AIJsonUtilities.DefaultOptions);
            var db = _redis.GetDatabase();
            await db.StringSetAsync(
                KeyFor(conversationId),
                json,
                TimeSpan.FromSeconds(TtlSeconds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to save agent conversation history error_type={ErrorType}",
                ex.GetType().Name);
        }
    }
}
