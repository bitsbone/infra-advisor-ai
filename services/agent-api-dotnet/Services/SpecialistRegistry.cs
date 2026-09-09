using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace InfraAdvisor.AgentApi.Services;

// Router + 5 tool-partitioned specialists, wired natively through
// Microsoft.Agents.AI.Workflows' HandoffWorkflowBuilder — the .NET
// equivalent of Python's router + _TOOL_PARTITIONS pattern
// (services/agent-api/src/agent.py).
//
// Deliberately ONE directed edge per specialist (router -> specialistX),
// never the reverse and never specialist-to-specialist: Python's
// specialists never hand off further, they finish the turn once picked.
// AddParticipants(agents) would wire every agent to every other agent,
// which is NOT the desired shape here.
//
// Session/history model: stateless per call, matching Python exactly.
// HandoffAgentExecutor keeps its own AgentSession/conversation state inside
// the Workflow's internal (in-memory, per-run) StatefulExecutor state —
// there is no serialize/deserialize surface for it comparable to
// AgentSessionStore's Redis round trip for a single ChatClientAgent's
// AgentThread, and building a Redis-backed CheckpointManager to persist
// that internal state was ruled out as unwarranted new-subsystem scope for
// this demo app (ilspycmd-confirmed against the installed 1.20.0 package —
// HandoffAgentExecutor.AgentSessionKey / HandoffStartExecutor.TakeTurnAsync).
// Instead AgentService loads/saves full ChatMessage history itself (same
// AgentSessionStore Redis round trip already in place) and passes the full
// history as the List<ChatMessage> input on every RunStreamingAsync call —
// each call's internal workflow state starts empty and is discarded when
// that call completes.
public class SpecialistRegistry
{
    // Mirrors Python's _TOOL_PARTITIONS exactly (services/agent-api/src/agent.py).
    // null = no filter (all tools) — used by "general" and, separately, by
    // the router (which additionally gets zero tools — see BuildAsync).
    private static readonly IReadOnlyDictionary<string, string[]?> ToolPartitions = new Dictionary<string, string[]?>
    {
        ["engineering"] = new[]
        {
            "get_bridge_condition", "get_disaster_history", "search_txdot_open_data",
            "get_water_infrastructure", "get_energy_infrastructure", "get_ercot_energy_storage",
            "search_project_knowledge", "draft_document",
        },
        ["water_energy"] = new[]
        {
            "get_water_infrastructure", "get_energy_infrastructure", "get_ercot_energy_storage",
            "get_disaster_history", "search_project_knowledge", "draft_document",
        },
        ["business_development"] = new[]
        {
            "get_procurement_opportunities", "get_contract_awards",
            "search_web_procurement", "search_project_knowledge",
        },
        ["document"] = new[]
        {
            "draft_document", "search_project_knowledge", "get_bridge_condition",
            "get_water_infrastructure", "get_energy_infrastructure",
        },
        ["general"] = null,
    };

    // Fallback prompt text per specialist, used only when the Datadog
    // Prompt Registry is unreachable/disabled — real parity with Python
    // comes from both backends reading the same registry entries once
    // DD_PROMPT_MANAGEMENT_ENABLED=true, so these don't need to be
    // byte-identical to Python's _SPECIALIST_SYSTEM_PROMPTS.
    private static readonly IReadOnlyDictionary<string, string> FallbackPrompts = new Dictionary<string, string>
    {
        ["router"] =
            "You are the InfraAdvisor router. Classify the user's request into exactly one " +
            "specialist domain and hand off immediately — never answer the question yourself. " +
            "Domains: engineering (bridges, highways, rail, structural), water_energy (water " +
            "systems, energy infrastructure, disasters), business_development (procurement, " +
            "contract awards, RFPs), document (drafting SOWs/reports/memos), general (anything " +
            "else, or requests spanning multiple domains).",
        ["engineering"] =
            "You are an InfraAdvisor civil/structural engineering specialist covering bridges " +
            "(FHWA NBI), highways/rail (TxDOT), disasters (FEMA), water, and energy infrastructure.",
        ["water_energy"] =
            "You are an InfraAdvisor water and energy infrastructure specialist covering water " +
            "systems (EPA SDWIS/TWDB), energy (EIA/ERCOT), and disaster history.",
        ["business_development"] =
            "You are an InfraAdvisor business development specialist covering federal procurement " +
            "intelligence (SAM.gov, USASpending.gov), contract awards, and open opportunities.",
        ["document"] =
            "You are an InfraAdvisor document drafting specialist producing SOWs, reports, and " +
            "memos grounded in firm knowledge and relevant engineering/water/energy data.",
        // Carried over verbatim from the old single-merged-agent fallback
        // prompt (previously Program.cs's AgentSystemPrompt const) — the
        // "general" specialist is its direct successor: same "every tool
        // available" shape, same guidelines and few-shot tool-call examples.
        ["general"] =
            "You are InfraAdvisor, a technical AI assistant for consultants across " +
            "AEC/O&M (Architecture, Engineering, Construction / Operations & Maintenance) " +
            "practice areas at a global infrastructure consulting firm.\n\n" +
            "Your expertise spans the full AEC/O&M project lifecycle: feasibility and planning, " +
            "civil and structural engineering (bridges, highways, rail), MEP and environmental systems " +
            "(water, wastewater, energy), construction project delivery, asset operations and maintenance, " +
            "and management advisory (program management, BD, risk, compliance).\n\n" +
            "You have access to tools covering bridges (FHWA NBI), disasters (FEMA), energy (EIA/ERCOT), " +
            "water systems (EPA SDWIS/TWDB), Texas transportation (TxDOT), firm knowledge base, " +
            "document drafting, and federal procurement intelligence (SAM.gov, USASpending.gov).\n\n" +
            "Guidelines:\n" +
            "1. Always cite the data source for factual claims (NBI structure numbers, PWSID, EIA plant IDs, " +
            "FEMA declaration IDs, USASpending award IDs, SAM.gov solicitation numbers).\n" +
            "2. Sort assets by descending risk: bridges by ascending sufficiency rating; water systems by " +
            "descending violation count.\n" +
            "3. Flag material risks explicitly — scour vulnerability, load rating deficiencies, repeat flood " +
            "events, SDWA violations, grid stress periods.\n" +
            "4. For business development queries, always call get_contract_awards before get_procurement_opportunities " +
            "— understanding who won similar work informs positioning for open opportunities.\n" +
            "5. When search_web_procurement returns results, flag medium-confidence extractions explicitly.\n" +
            "6. NEVER ask the user for a date range — procurement tools default to the last 12 months automatically.\n" +
            "7. For document drafts, call search_project_knowledge first for relevant templates and prior project context.\n" +
            "8. Do not speculate about asset conditions not in the data — say \"not available in the dataset\".\n" +
            "9. Respond in the same language the user writes in. Keep responses concise for data lookups; " +
            "detailed for engineering analysis and document drafts.\n\n" +
            "Examples of correct tool calls:\n\n" +

            "User: \"Worst-rated bridges in California\"\n" +
            "→ get_bridge_condition(state_code=\"06\", max_lowest_rating=4, limit=25)\n" +
            "  (Note: state_code is 2-char FIPS with leading zero. CA=06, TX=48, FL=12, NY=36.)\n\n" +

            "User: \"Find recent federal highway construction awards in Texas under NAICS 237310, " +
            "then list open opportunities matching the same NAICS\"\n" +
            "→ get_contract_awards(query=\"highway construction\", geography=\"TX\", naics_codes=[\"237310\"])\n" +
            "→ get_procurement_opportunities(query=\"highway construction\", geography=\"TX\", naics_codes=[\"237310\"])\n" +
            "  (BD pairing rule: awards FIRST so competitive context informs the open-opportunity " +
            "list. Never ask the user for a date range.)\n\n" +

            "User: \"Which Texas community water systems have SDWA violations serving 10K+ people?\"\n" +
            "→ get_water_infrastructure(query_type=\"violations\", states=[\"TX\"], " +
            "system_types=[\"CWS\"], has_violations=true, min_population_served=10000)\n" +
            "  (query_type=\"violations\" — not \"water_systems\". CWS = Community Water System.)\n\n" +

            "User: \"Draft an SOW for an IH-35 bridge rehabilitation project\"\n" +
            "→ search_project_knowledge(query=\"bridge rehabilitation SOW IH-35\", " +
            "document_types=[\"sow\", \"case_study\"])\n" +
            "→ draft_document(document_type=\"scope_of_work\", context={...retrieved snippets...}, " +
            "project_name=\"IH-35 Bridge Rehabilitation\")\n" +
            "  (ALWAYS call search_project_knowledge first to pull templates + prior project " +
            "context; pass retrieved content into context for draft_document.)\n\n" +

            "User: \"Texas renewable energy generation share over the last 5 years\"\n" +
            "→ get_energy_infrastructure(states=[\"TX\"], data_series=\"fuel_mix\", " +
            "year_from=2019, year_to=2024)\n" +
            "  (data_series=\"fuel_mix\" returns % share by fuel — what \"renewable share\" means. " +
            "Use \"generation\" for raw MWh, \"capacity\" for installed MW.)",
    };

    private readonly IChatClient _chatClient;
    private readonly McpClientHolder _mcpHolder;
    private readonly DatadogPromptManagementClient _promptClient;
    private readonly PromptVersionFlags _promptFlags;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _otelSourceName;
    private readonly object _lock = new();

    private Workflow? _workflow;
    private long _builtForMcpGeneration = -1;
    private long _builtForPromptGeneration = -1;

    // Keyed by the same agent id used as ChatClientAgentOptions.Id / the
    // workflow's ExecutorId (router, specialist-engineering, ...) — lets
    // both GET /prompts/status and the per-turn version badge (Workstream 4)
    // look up "which prompt answered this turn" from a WorkflowOutputEvent's
    // ExecutorId.
    public IReadOnlyDictionary<string, PromptHolder> PromptHolders { get; }

    private readonly IReadOnlyDictionary<string, AgentHolder> _agentHolders;

    public SpecialistRegistry(
        IChatClient chatClient,
        McpClientHolder mcpHolder,
        DatadogPromptManagementClient promptClient,
        PromptVersionFlags promptFlags,
        ILoggerFactory loggerFactory,
        string otelSourceName)
    {
        _chatClient = chatClient;
        _mcpHolder = mcpHolder;
        _promptClient = promptClient;
        _promptFlags = promptFlags;
        _loggerFactory = loggerFactory;
        _otelSourceName = otelSourceName;

        var promptHolders = new Dictionary<string, PromptHolder>();
        var agentHolders = new Dictionary<string, AgentHolder>();

        // "router" and "specialist-<domain>" are the exact prompt_ids
        // Python's seed script already created in the shared Datadog Prompt
        // Registry — see the plan's Workstream 1 (elegant-stargazing-metcalfe.md).
        promptHolders["router"] = new PromptHolder(
            "router", _promptClient, _promptFlags, FallbackPrompts["router"],
            _loggerFactory.CreateLogger<PromptHolder>());
        agentHolders["router"] = new AgentHolder(
            _chatClient, _mcpHolder, promptHolders["router"],
            agentName: "router", otelSourceName: _otelSourceName,
            allowedToolNames: new HashSet<string>()); // zero tools — router only classifies + hands off

        foreach (var (domain, tools) in ToolPartitions)
        {
            var agentId = $"specialist-{domain}";
            var promptId = agentId;
            promptHolders[agentId] = new PromptHolder(
                promptId, _promptClient, _promptFlags, FallbackPrompts[domain],
                _loggerFactory.CreateLogger<PromptHolder>());
            agentHolders[agentId] = new AgentHolder(
                _chatClient, _mcpHolder, promptHolders[agentId],
                agentName: agentId, otelSourceName: _otelSourceName,
                allowedToolNames: tools is null ? null : new HashSet<string>(tools));
        }

        PromptHolders = promptHolders;
        _agentHolders = agentHolders;
    }

    // Warms every prompt holder at startup (bounded, same pattern
    // Program.cs already applies to the single prompt holder today) —
    // called once from Program.cs before the app starts serving.
    public async Task WarmUpAsync(CancellationToken ct)
    {
        foreach (var holder in PromptHolders.Values)
        {
            try { await holder.RefreshAsync(ct); }
            catch (OperationCanceledException) { /* fallback applies; periodic refresh retries */ }
        }
    }

    public async Task RefreshAllPromptsAsync(CancellationToken ct)
    {
        foreach (var holder in PromptHolders.Values)
            await holder.RefreshAsync(ct);
    }

    public async Task<Workflow> GetWorkflowAsync(CancellationToken ct)
    {
        // Building every AgentHolder's agent also lazily resolves
        // McpClientHolder + refreshes each PromptHolder as needed — same
        // cheap-but-not-free construction AgentHolder already documents.
        var agents = new Dictionary<string, AIAgent>();
        foreach (var (id, holder) in _agentHolders)
            agents[id] = await holder.GetAgentAsync(ct);

        var mcpGen = _mcpHolder.Generation;
        var promptGen = PromptHolders.Values.Sum(h => h.Generation);

        lock (_lock)
        {
            if (_workflow is not null && _builtForMcpGeneration == mcpGen && _builtForPromptGeneration == promptGen)
                return _workflow;
        }

        var fresh = BuildWorkflow(agents);

        lock (_lock)
        {
            if (_workflow is not null && _builtForMcpGeneration == mcpGen && _builtForPromptGeneration == promptGen)
                return _workflow;
            _workflow = fresh;
            _builtForMcpGeneration = mcpGen;
            _builtForPromptGeneration = promptGen;
            return _workflow;
        }
    }

    // Per-request, per-user targeted variant (see PromptHolder.
    // ResolveForRequestAsync / AgentHolder.GetAgentForRequestAsync) —
    // evaluates every specialist's prompt flag with the calling user's
    // targeting context (targetingKey/attributes, e.g. the demo job_role
    // attribute). Returns the cached default workflow, with no extra
    // construction, when nothing resolved differently for this user (the
    // common case — no per-user targeting configured, or this user has no
    // pinned override). Only builds a one-off workflow, not cached, when at
    // least one specialist's prompt actually differs for this user —
    // AgentHolder's own construction cost is "cheap but not zero," an
    // accepted, deliberate trade-off for a demo app's traffic level rather
    // than a silent perf regression.
    public async Task<Workflow> GetWorkflowForRequestAsync(
        string? targetingKey, IReadOnlyDictionary<string, string>? attributes, CancellationToken ct)
    {
        if (targetingKey is null && (attributes is null || attributes.Count == 0))
            return await GetWorkflowAsync(ct);

        var agents = new Dictionary<string, AIAgent>();
        var anyOverride = false;
        foreach (var (id, holder) in _agentHolders)
        {
            var (agent, isOverride) = await holder.GetAgentForRequestAsync(targetingKey, attributes, ct);
            agents[id] = agent;
            anyOverride |= isOverride;
        }

        return anyOverride ? BuildWorkflow(agents) : await GetWorkflowAsync(ct);
    }

    private static Workflow BuildWorkflow(IReadOnlyDictionary<string, AIAgent> agents)
    {
        var router = agents["router"];
        var builder = new HandoffWorkflowBuilder(router);
        foreach (var domain in ToolPartitions.Keys)
        {
            var specialist = agents[$"specialist-{domain}"];
            builder.WithHandoff(router, specialist, handoffReason: $"Route here for {domain.Replace('_', ' ')} questions.");
        }

        // HandoffWorkflowBuilder has no WithOpenTelemetry hook of its own
        // (ilspycmd-confirmed: Build() constructs its own internal
        // WorkflowBuilder with no way to inject a WorkflowTelemetryContext
        // from here) — workflow-level spans (SuperStep/routing) are not
        // available at 1.20.0 through this builder. Per-specialist
        // invoke_agent/chat/tool spans still come through unchanged: each
        // AIAgent built by AgentHolder already has its own
        // .AsBuilder().UseOpenTelemetry() applied before it ever reaches
        // the workflow.
        return builder.Build();
    }
}
