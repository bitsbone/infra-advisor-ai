using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Azure;
using Azure.AI.OpenAI;
using System.ClientModel.Primitives;
using InfraAdvisor.AgentApi.Models;
using InfraAdvisor.AgentApi.Observability;
using InfraAdvisor.AgentApi.Services;
using InfraAdvisor.AgentApi.Services.Evaluators;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ── Environment variable helpers ──────────────────────────────────────────────
static string Env(string key, string? fallback = null) =>
    Environment.GetEnvironmentVariable(key)
    ?? fallback
    ?? throw new InvalidOperationException($"Required environment variable '{key}' is not set.");

static string EnvOr(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) ?? fallback;

// Prefer the x-datadog-trace-id request header (RUM injects the authoritative
// 64-bit decimal DD trace ID). Fall back to converting OTel lower-64-bit hex
// to decimal so direct API tests (no RUM) still get a usable identifier.
static string? GetDdTraceId(HttpContext ctx, Activity? activity)
{
    var header = ctx.Request.Headers["x-datadog-trace-id"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(header)) return header;
    var hex = activity?.TraceId.ToString();
    if (hex is not { Length: 32 }) return hex;
    return ulong.TryParse(hex[16..], System.Globalization.NumberStyles.HexNumber, null, out var lo)
        ? lo.ToString() : hex;
}

static string? GetDdSpanId(Activity? activity)
{
    var hex = activity?.SpanId.ToString();
    if (hex is null) return null;
    return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var id)
        ? id.ToString() : hex;
}

// ── Configuration ─────────────────────────────────────────────────────────────
var azureEndpoint = Env("AZURE_OPENAI_ENDPOINT");
var azureApiKey   = Env("AZURE_OPENAI_API_KEY");
var azureDeployment = EnvOr("AZURE_OPENAI_DEPLOYMENT", "gpt-5.4-mini");
var azureEmbeddingDeployment = EnvOr("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "text-embedding-3-small");
var availableModelsRaw = EnvOr("AVAILABLE_MODELS", "gpt-5.4-mini");
var mcpServerUrl = EnvOr("MCP_SERVER_URL", "http://mcp-server-dotnet.infra-advisor.svc.cluster.local:8000/mcp");
var redisHost = EnvOr("REDIS_HOST", "redis.infra-advisor.svc.cluster.local");
var redisPort = int.Parse(EnvOr("REDIS_PORT", "6379"));
var redisPassword = Environment.GetEnvironmentVariable("REDIS_PASSWORD");
var kafkaBootstrapServers = EnvOr("KAFKA_BOOTSTRAP_SERVERS", "kafka-cluster-kafka-bootstrap.kafka.svc.cluster.local:9092");
// Whisper lives on a SEPARATE Cognitive Services account/region — whisper-001's
// "Standard" deployment SKU isn't offered in every region (confirmed absent in
// eastus via the Cognitive Services models API), so it has its own account in
// a region that does support it. See infra/bicep/modules/azure-openai.bicep.
var azureWhisperEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_WHISPER_ENDPOINT");
var azureWhisperApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_WHISPER_API_KEY");

Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", azureDeployment);
Environment.SetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS", kafkaBootstrapServers);

// ── OpenTelemetry + Logging ───────────────────────────────────────────────────
TelemetrySetup.Configure(builder);

// ── AppState ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new AppState());

// ── Redis ─────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var cfg = new ConfigurationOptions
    {
        EndPoints = { $"{redisHost}:{redisPort}" },
        Password = redisPassword,
        AbortOnConnectFail = false,
        ConnectTimeout = 5000,
        SyncTimeout = 5000,
    };
    try { return ConnectionMultiplexer.Connect(cfg); }
    catch (Exception ex)
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        loggerFactory.CreateLogger("Redis").LogWarning("Redis connection failed error_type={ErrorType}", ex.GetType().Name);
        return ConnectionMultiplexer.Connect(cfg);
    }
});

// ── Azure OpenAI client (used by both M.E.AI's IChatClient and SuggestionService) ─
// Retry policy is explicitly bounded rather than left at the SDK default.
// Traces 1722495265582310941 / 1891183475693376788 showed single "chat"
// spans running 42-52s under sustained 429s — not wrong (retrying is
// correct), but uncoordinated with the 300s nginx proxy timeout shared by
// the rest of the /query/stream pipeline (MCP calls, embeddings, DB
// writes). MaxRetries caps the worst-case total wait, leaving headroom
// under the proxy ceiling (the SDK's ClientRetryPolicy doesn't expose a
// separate max-per-delay knob in this System.ClientModel version — bounding
// retry count is the lever available). RateLimitObservabilityPolicy makes
// each individual 429 visible (as a counter + log line) instead of
// vanishing into the SDK's internal retry loop.
var llmRateLimitMeter = new Meter(TelemetrySetup.ActivitySourceName);
var llmRateLimitCounter = llmRateLimitMeter.CreateCounter<long>(
    "infra_advisor.llm.rate_limited",
    description: "Count of individual Azure OpenAI 429 responses observed by the retry pipeline.");

builder.Services.AddSingleton(sp =>
{
    var options = new AzureOpenAIClientOptions
    {
        RetryPolicy = new ClientRetryPolicy(maxRetries: 4),
    };
    options.AddPolicy(
        new RateLimitObservabilityPolicy(
            llmRateLimitCounter, sp.GetRequiredService<ILogger<RateLimitObservabilityPolicy>>()),
        PipelinePosition.PerTry);
    return new AzureOpenAIClient(new Uri(azureEndpoint), new AzureKeyCredential(azureApiKey), options);
});

// Second AzureOpenAIClient, keyed "whisper" — separate account/region from
// the main client above (see azureWhisperEndpoint comment). Registered only
// when both env vars are present; AgentService treats a missing keyed
// client as "voice transcription disabled" rather than failing startup,
// since this is an additive feature, not a core dependency.
if (!string.IsNullOrWhiteSpace(azureWhisperEndpoint) && !string.IsNullOrWhiteSpace(azureWhisperApiKey))
{
    builder.Services.AddKeyedSingleton("whisper", (sp, _) =>
        new AzureOpenAIClient(new Uri(azureWhisperEndpoint), new AzureKeyCredential(azureWhisperApiKey)));
}

// ── MCP client holder — lazy connect with reconnect-on-session-expired ─────
// Previously we did a synchronous McpClient.CreateAsync at startup and
// registered the resulting client as a singleton. That worked fine until
// mcp-server-dotnet restarted (any rollout, OOM, AKS rebalance) — the
// cached client's session ID stopped resolving on the new server pod and
// every tool call returned HTTP 404. The only mitigation was to manually
// `kubectl rollout restart deployment/agent-api-dotnet`.
//
// McpClientHolder fixes that: it lazy-connects on first use, exposes
// RefreshAsync() to recreate the client + tool list on demand, and
// returns a monotonically-incrementing Generation that the AgentHolder
// uses as a cache key. AgentService catches session-expired exceptions
// and calls RefreshAsync — first request after an mcp-server restart
// pays one extra round trip; everything after is normal.
// Register the named HttpClient used by McpClientHolder so OTel's
// AddHttpClientInstrumentation() handler is in the pipeline (spans appear as
// HTTP child spans under each tool_call in Datadog APM).
// Explicit finite timeout (default HttpClient.Timeout is 100s) so a
// genuinely hung mcp-server-dotnet call fails within a bounded window
// instead of tying up the streaming pipeline for the full default.
builder.Services.AddHttpClient("mcp-dotnet")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));

// Downloads chat-attachment audio blobs (already uploaded via the Python
// agent-api's POST /media/upload) so AgentService can hand the bytes to
// Azure OpenAI Whisper for transcription. Separate named client from
// mcp-dotnet since it talks to Azure Blob Storage, not the MCP server.
builder.Services.AddHttpClient("agent-media-download")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton(sp => new McpClientHolder(
    serverUrl: mcpServerUrl,
    clientName: "infra-advisor-agent-api-dotnet",
    logger: sp.GetRequiredService<ILogger<McpClientHolder>>(),
    httpClientFactory: sp.GetRequiredService<IHttpClientFactory>(),
    loggerFactory: sp.GetRequiredService<ILoggerFactory>()));

// ── IChatClient pipeline (M.E.AI) ─────────────────────────────────────────────
// .UseFunctionInvocation() runs the tool-call loop and emits execute_tool spans.
// .UseOpenTelemetry()  emits chat spans on the "Experimental.Microsoft.Extensions.AI"
// ActivitySource (registered in TelemetrySetup.cs).
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var mcpClientHolder = sp.GetRequiredService<McpClientHolder>();
    var toolDiagnosticsLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ToolInvocationDiagnostics");

    return sp.GetRequiredService<AzureOpenAIClient>()
        .GetChatClient(azureDeployment)
        .AsIChatClient()
        .AsBuilder()
        .UseFunctionInvocation(configure: fic =>
        {
            // Diagnostic wrapper around every MCP tool call — investigating a
            // recurring near-instant TaskCanceledException on execute_tool
            // (Datadog Error Tracking issue 1d5322a6-5065-11f1-91de-da7ad0900003),
            // seen specifically from MAUI clients. The bare framework exception
            // carries no app context, so this tags the SAME execute_tool
            // Activity the framework already creates (Activity.Current here is
            // that exact span — FunctionInvokingChatClient starts it before
            // invoking this delegate) and logs a structured line distinct from
            // the generic HTTP request logs.
            fic.FunctionInvoker = async (context, ct) =>
            {
                var toolName = context.Function.Name;
                var ctAlreadyCancelled = ct.IsCancellationRequested;
                var mcpGeneration = mcpClientHolder.Generation;
                var sw = Stopwatch.StartNew();

                Activity.Current?.SetTag("tool.ct_already_cancelled", ctAlreadyCancelled);
                Activity.Current?.SetTag("tool.mcp_generation", mcpGeneration);

                try
                {
                    var result = await context.Function.InvokeAsync(context.Arguments, ct);
                    Activity.Current?.SetTag("tool.elapsed_ms", sw.Elapsed.TotalMilliseconds);
                    return result;
                }
                catch (Exception ex)
                {
                    Activity.Current?.SetTag("tool.elapsed_ms", sw.Elapsed.TotalMilliseconds);
                    toolDiagnosticsLogger.LogWarning(ex,
                        "Tool call failed tool={ToolName} elapsed_ms={ElapsedMs} ct_already_cancelled={CtAlreadyCancelled} mcp_generation={McpGeneration}",
                        toolName, sw.Elapsed.TotalMilliseconds, ctAlreadyCancelled, mcpGeneration);
                    throw; // preserve existing framework + ClassifyStreamError behavior
                }
            };
        })
        .UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = TelemetryPrivacy.EnableSensitiveData)
        .Build();
});

// ── IEmbeddingGenerator pipeline (M.E.AI) ─────────────────────────────────────
// Azure OpenAI embedding deployment behind the M.E.AI provider-neutral
// interface. .UseOpenTelemetry() emits an "embeddings" span (gen_ai.operation
// .name=embeddings) on the same Experimental.Microsoft.Extensions.AI source
// as chat/tool spans — DD LLMObs auto-classifies it as the "embedding" kind.
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    sp.GetRequiredService<AzureOpenAIClient>()
        .GetEmbeddingClient(azureEmbeddingDeployment)
        .AsIEmbeddingGenerator()
        .AsBuilder()
        .UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = TelemetryPrivacy.EnableSensitiveData)
        .Build());

// ── Agent (MAF) ───────────────────────────────────────────────────────────────
// Single ChatClientAgent with all MCP tools. The model picks which tools
// to call per turn. .UseOpenTelemetry(sourceName:) emits the invoke_agent
// span on the ActivitySource registered in TelemetrySetup.cs.
const string AgentSystemPrompt =
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
    // ── Few-shot tool-call examples ─────────────────────────────────────────────
    // Concrete worked patterns the model can anchor on for the high-error
    // decision points: FIPS state codes (not 2-letter abbrevs), AEC NAICS
    // codes (not category names), the BD chain pattern, water query_type
    // dispatch, the document-drafting chain. Keeping these tight — verbosity
    // here costs every request's input tokens.
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
    "Use \"generation\" for raw MWh, \"capacity\" for installed MW.)";

// ── Prompt management: fetch system prompt from DD's Prompt Registry ───────
// PromptHolder refreshes periodically (PromptRefreshBackgroundService, ~60s)
// so a version bump in the Datadog UI — including one pinned via a
// prompt-version.* Feature Flag (PromptVersionFlags) — reaches a running
// pod without a redeploy. Both are constructed directly here (not resolved
// from DI) so the ActivityListener below can read PromptHolder's live state
// before builder.Build() runs; PromptHolder is then registered as the
// shared DI singleton so AgentHolder, the background refresh, and
// GET /prompts/status all see the same instance.
using var promptBootstrapHttp = new HttpClient();
var promptManagementClient = new DatadogPromptManagementClient(
    promptBootstrapHttp, NullLogger<DatadogPromptManagementClient>.Instance);
var promptVersionFlags = new PromptVersionFlags(NullLogger<PromptVersionFlags>.Instance);
var promptHolder = new PromptHolder(
    promptManagementClient, promptVersionFlags, AgentSystemPrompt, NullLogger<PromptHolder>.Instance);
// Bounded startup-time warm-up: must never block app startup past this
// deadline regardless of what hangs inside (Feature Flags provider init,
// registry HTTP call, DNS). PromptHolder's own fallback still applies —
// the background refresh (PromptRefreshBackgroundService) retries after.
using (var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    try
    {
        await promptHolder.RefreshAsync(startupCts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[prompt-management] initial warm-up did not complete within 5s — using fallback until the periodic refresh succeeds.");
    }
}

builder.Services.AddSingleton(promptHolder);
builder.Services.AddSingleton(promptVersionFlags);
builder.Services.AddHostedService<PromptRefreshBackgroundService>();

// AgentHolder builds (and rebuilds) the ChatClientAgent against the current
// McpClientHolder tool list and PromptHolder's current prompt. Both holders'
// Generation are tracked so the agent rebuilds once per change, not per
// request — see AgentHolder.
builder.Services.AddSingleton(sp => new AgentHolder(
    chatClient:     sp.GetRequiredService<IChatClient>(),
    mcpHolder:      sp.GetRequiredService<McpClientHolder>(),
    promptHolder:   sp.GetRequiredService<PromptHolder>(),
    agentName:      "infra-advisor",
    otelSourceName: TelemetrySetup.ActivitySourceName));

// ── Prompt-version/tracking + agent-span capture ────────────────────────
// One ActivityListener does three jobs:
//   1. Stamps a content-derived prompt version on chat + invoke_agent spans
//      without exporting the prompt template itself.
//   2. Stamps DD LLM Observability's prompt-tracking attribute (the
//      OTel-path equivalent of ddtrace Python's LLMObs.annotate(prompt=...))
//      so the LLM Obs UI shows which prompt template/version produced the
//      span, and whether it came from the registry or the local fallback.
//   3. Captures the invoke_agent span's (trace_id, span_id) into an
//      AsyncLocal so AgentService can attach external-eval scores to the
//      AGENT span (not the HTTP root) — DD requires both IDs on the
//      eval-metric API's join_on.span field.
static string ShortContentHash(string text)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
    return Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant();
}

// Reads PromptHolder.Current fresh on every activity rather than closing
// over a startup-time snapshot — required now that PromptHolder refreshes
// periodically; otherwise span tagging would go stale the moment a
// background refresh picked up a new version.
ActivitySource.AddActivityListener(new ActivityListener
{
    ShouldListenTo = source =>
        source.Name == "Experimental.Microsoft.Extensions.AI" ||
        source.Name == TelemetrySetup.ActivitySourceName,
    ActivityStarted = activity =>
    {
        if (activity.OperationName is "invoke_agent" or "chat")
        {
            var current = promptHolder.Current;
            // A content hash preserves prompt-version correlation without
            // copying the system prompt into exported span attributes.
            var promptVersion = current.Source is "registry" or "flag-pinned"
                ? $"{current.Source}-{current.Version}"
                : "v1-" + ShortContentHash(current.Template);
            var promptTrackingJson = JsonSerializer.Serialize(new
            {
                id = PromptHolder.PromptId,
                version = promptVersion,
                template = current.Template,
                source = current.Source,
            });
            activity.SetTag("prompt.version", promptVersion);
            activity.SetTag("_dd.ml_obs.prompt_tracking", promptTrackingJson);
        }
        if (activity.OperationName == "invoke_agent")
            AgentSpanContext.Capture(activity);

        // DD LLM Observability session/conversation grouping (OTel path)
        // requires gen_ai.conversation.id on every gen_ai span in the trace,
        // not just the root — see AmbientSessionContext for why this can't
        // be set at each auto-instrumented span's call site directly.
        if (AmbientSessionContext.Current is { } sessionId)
            activity.SetTag("gen_ai.conversation.id", sessionId);
    },
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
});
Console.WriteLine($"[prompt-management] system prompt source={promptHolder.Current.Source} " +
                  $"version={promptHolder.Current.Version} ({promptHolder.Current.Template.Length} chars)");

// ── Business metrics meter ────────────────────────────────────────────────────
// Shared meter for endpoint-level counters (conversation + tool counters
// live in AgentService since they need response data; feedback counter is
// emitted from the /feedback endpoint below). Same name as the OTel meter
// pipeline already AddMeter's, so DD picks them up via OTLP automatically.
var bizMeter = new Meter(TelemetrySetup.ActivitySourceName);
var feedbackCounter = bizMeter.CreateCounter<long>(
    "infra_advisor.feedback.submitted",
    description: "User feedback submissions via /feedback. Tagged with rating.");

// ── Core services ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<AgentSessionStore>();
builder.Services.AddSingleton<RetrievalService>();
// In-memory ring buffer of recent eval-submission outcomes, surfaced by the
// admin diagnostics panel via GET /eval/status. Single instance shared with
// every DatadogEvalsClient submission so the panel can see the most recent
// 50 attempts across all evaluators.
builder.Services.AddSingleton<EvalSubmissionLog>();
builder.Services.AddHttpClient<DatadogEvalsClient>();
// AI Guard: HTTP API path (no LangChain-equivalent auto-integration exists
// for Microsoft Agent Framework). Ring buffer mirrors EvalSubmissionLog —
// surfaced via GET /ai-guard/status since AI Guard's HTTP API sends no
// traces to Datadog on its own (see DatadogAiGuardClient).
builder.Services.AddSingleton<AiGuardSubmissionLog>();
builder.Services.AddHttpClient<DatadogAiGuardClient>();
builder.Services.AddSingleton<IResponseEvaluator, CitationPresentEvaluator>();
builder.Services.AddSingleton<IResponseEvaluator, BdToolOrderingEvaluator>();
builder.Services.AddSingleton<IResponseEvaluator, ToolRoutingAccuracyEvaluator>();
// LLM-as-judge — wrappers around Microsoft.Extensions.AI.Evaluation.Quality.
// Uses the same IChatClient as the agent (gpt-5.4-mini by default) for the judge call;
// per-eval cost is one extra inference call on each sampled trace.
builder.Services.AddSingleton<IResponseEvaluator, MeaiRelevanceEvaluator>();
builder.Services.AddSingleton<IResponseEvaluator, MeaiGroundednessEvaluator>();
builder.Services.AddSingleton<IContractAwardsEventPublisher, ContractAwardsEventPublisher>();
builder.Services.AddSingleton<AgentService>();
builder.Services.AddSingleton<SuggestionService>();
builder.Services.AddSingleton<ConversationService>();
builder.Services.AddSingleton<MediaService>();

// ── Background services ───────────────────────────────────────────────────────
builder.Services.AddHostedService<KafkaConsumerService>();
builder.Services.AddHostedService<SuggestionPoolMaintenanceService>();

// ── JSON snake_case globally ──────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

// ── JWT auth (shared secret with auth-api) ────────────────────────────────────
// Same JWT_SECRET / HS256 algorithm as services/auth-api/src/auth.py.
// Tokens issued by /auth/login validate here without a round-trip.
// Fails closed at startup if JWT_SECRET isn't set — better than running
// with an empty key and silently accepting forged tokens.
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? throw new InvalidOperationException(
        "JWT_SECRET env var is required — share the value with auth-api.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.RequireHttpsMetadata = false;  // TLS handled at the nginx ingress
        opts.SaveToken = false;
        opts.MapInboundClaims = false;      // keep JWT claim names as-is ("sub" stays "sub")
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();

// ── Rate limiting ─────────────────────────────────────────────────────────────
// Per-user (or per-IP for unauthenticated) sliding window on /query and
// /query/stream. Keyed by JWT `sub` claim when available — same logic the
// Python service uses, so a user can't multiply their quota by switching IPs.
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = 429;
    opts.AddPolicy("query", httpContext =>
    {
        var key = httpContext.User?.FindFirst("sub")?.Value
                  ?? httpContext.Connection.RemoteIpAddress?.ToString()
                  ?? "anon";
        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: key,
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            });
    });
    // Mirrors Python agent-api's @limiter.limit("10/minute") on POST /media/upload.
    opts.AddPolicy("media", httpContext =>
    {
        var key = httpContext.User?.FindFirst("sub")?.Value
                  ?? httpContext.Connection.RemoteIpAddress?.ToString()
                  ?? "anon";
        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: key,
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            });
    });
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ── Startup probes ────────────────────────────────────────────────────────────
var appState = app.Services.GetRequiredService<AppState>();
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var conversationService = app.Services.GetRequiredService<ConversationService>();

try { await conversationService.InitializeAsync(); }
catch (Exception ex) { startupLogger.LogWarning("Conversation DB init failed error_type={ErrorType}", ex.GetType().Name); }

var availableModels = availableModelsRaw
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToList();
if (availableModels.Count == 0) availableModels.Add("gpt-5.4-mini");
appState.AvailableModels.AddRange(availableModels);

// MCP connects lazily on the first /query (via McpClientHolder). We mark
// "connected" optimistically here so the /query gate doesn't reject the
// very first call before the holder has run its connect — if the holder
// can't reach mcp-server-dotnet it surfaces a clear exception inside the
// handler. Azure OpenAI client construction is synchronous and pre-
// flighted by DI.
appState.McpConnected = true;
appState.LlmConnected = true;

// Pre-warm the MCP connection in the background so the first /query
// doesn't pay the connect latency. Best-effort; failure is logged and
// the next /query will retry.
_ = Task.Run(async () =>
{
    try
    {
        var holder = app.Services.GetRequiredService<McpClientHolder>();
        await holder.GetClientAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning("MCP pre-warm failed error_type={ErrorType}", ex.GetType().Name);
    }
});

// ── Endpoints ─────────────────────────────────────────────────────────────────

app.MapPost("/query", async (
    QueryRequest body,
    HttpContext httpContext,
    AgentService agentService,
    MemoryService memoryService,
    ConversationService conversationSvc,
    AppState state) =>
{
    var headerSessionId = httpContext.Request.Headers["X-Session-ID"].FirstOrDefault();
    var rumSessionId = httpContext.Request.Headers["X-DD-RUM-Session-ID"].FirstOrDefault();
    var conversationId = httpContext.Request.Headers["X-Conversation-ID"].FirstOrDefault();
    var userId = SubClaim(httpContext);
    if (userId is null) return Results.Unauthorized();
    var sessionId = body.SessionId ?? headerSessionId ?? Guid.NewGuid().ToString();
    if (!string.IsNullOrWhiteSpace(conversationId))
    {
        var access = await conversationSvc.CheckOwnershipAsync(conversationId, userId);
        if (access == ConversationAccess.Unavailable)
            return Results.Problem(detail: "Conversation storage unavailable", statusCode: 503);
        if (access != ConversationAccess.Owned) return Results.NotFound();
    }
    List<AttachmentDto>? attachments;
    try
    {
        attachments = body.Attachments?.Select(AttachmentReferenceValidator.Validate).ToList();
    }
    catch (InvalidAttachmentReferenceException)
    {
        return Results.Problem(detail: "Invalid attachment reference", statusCode: 422);
    }
    if (!state.McpConnected || !state.LlmConnected)
        return Results.Problem(detail: "Agent not ready", statusCode: 503);

    var agentSessionKey = TenantSessionKey.Create(userId, !string.IsNullOrWhiteSpace(conversationId) ? conversationId : sessionId);

    string deployment;
    if (!string.IsNullOrWhiteSpace(body.Model) && state.AvailableModels.Contains(body.Model))
    {
        deployment = body.Model;
    }
    else
    {
        var sessionModel = await memoryService.GetSessionModelAsync(agentSessionKey);
        deployment = state.AvailableModels.Contains(sessionModel) ? sessionModel : state.DefaultModel;
    }

    AgentResult result;
    try
    {
        result = await agentService.RunAgentAsync(
            query: body.Query,
            sessionId: agentSessionKey,
            deployment: deployment,
            attachments: attachments,
            rumSessionId: rumSessionId,
            ct: httpContext.RequestAborted);
    }
    catch (Exception ex)
    {
        var errTraceId = GetDdTraceId(httpContext, Activity.Current);
        var publicError = PublicError.Unexpected(ex);
        app.Logger.LogWarning("Query failed error_type={ErrorType}", ex.GetType().Name);
        return Results.Problem(detail: publicError.Detail, statusCode: 500,
            extensions: new Dictionary<string, object?> { ["error_type"] = publicError.ErrorType, ["trace_id"] = errTraceId });
    }

    if (result.Blocked)
    {
        var blockedTraceId = GetDdTraceId(httpContext, Activity.Current);
        return Results.Problem(detail: result.BlockReason ?? "Blocked by AI Guard", statusCode: 403,
            extensions: new Dictionary<string, object?> { ["trace_id"] = blockedTraceId, ["blocked"] = true });
    }

    await memoryService.SetSessionModelAsync(agentSessionKey, deployment);

    var traceId = GetDdTraceId(httpContext, Activity.Current);
    var spanId = GetDdSpanId(Activity.Current);

    if (!string.IsNullOrWhiteSpace(conversationId))
    {
        await conversationSvc.SaveMessagesAsync(
            conversationId, userId, body.Query, result.Answer,
            result.Sources, traceId, spanId, attachments: attachments,
            artifacts: result.Artifacts ?? []);
    }

    return Results.Ok(new QueryResponse(
        Answer: result.Answer,
        Sources: result.Sources,
        TraceId: traceId,
        SpanId: spanId,
        SessionId: sessionId,
        Model: deployment,
        Artifacts: result.Artifacts ?? []));
}).RequireAuthorization().RequireRateLimiting("query");

// ── /query/stream — Server-Sent Events streaming variant ──────────────────────
// Same agent pipeline as /query but yields one SSE block per StreamEvent so
// the UI can show classify_domain / retrieve_best_practices / tool_call /
// tool_call_end / text_chunk / done events live. NGINX in front of this
// pod must skip buffering on this path — set in services/ui/nginx.conf and
// reinforced by the X-Accel-Buffering: no response header below.
//
// Trade-off vs /query: no mid-stream MCP-session-expired retry (text we
// already streamed can't be cleanly rewound). Clients can fall back to
// /query if the streaming path fails; resilient reconnect lives there.
app.MapPost("/query/stream", async (
    QueryRequest body,
    HttpContext httpContext,
    AgentService agentService,
    MemoryService memoryService,
    ConversationService conversationSvc,
    AppState state) =>
{
    var headerSessionId = httpContext.Request.Headers["X-Session-ID"].FirstOrDefault();
    var rumSessionId = httpContext.Request.Headers["X-DD-RUM-Session-ID"].FirstOrDefault();
    var conversationId = httpContext.Request.Headers["X-Conversation-ID"].FirstOrDefault();
    var userId = SubClaim(httpContext);
    if (userId is null) return Results.Unauthorized();
    var sessionId = body.SessionId ?? headerSessionId ?? Guid.NewGuid().ToString();
    if (!string.IsNullOrWhiteSpace(conversationId))
    {
        var access = await conversationSvc.CheckOwnershipAsync(conversationId, userId);
        if (access == ConversationAccess.Unavailable)
            return Results.Problem(detail: "Conversation storage unavailable", statusCode: 503);
        if (access != ConversationAccess.Owned) return Results.NotFound();
    }
    List<AttachmentDto>? attachments;
    try
    {
        attachments = body.Attachments?.Select(AttachmentReferenceValidator.Validate).ToList();
    }
    catch (InvalidAttachmentReferenceException)
    {
        return Results.Problem(detail: "Invalid attachment reference", statusCode: 422);
    }
    if (!state.McpConnected || !state.LlmConnected)
        return Results.Problem(detail: "Agent not ready", statusCode: 503);

    var agentSessionKey = TenantSessionKey.Create(userId, !string.IsNullOrWhiteSpace(conversationId) ? conversationId : sessionId);

    string deployment;
    if (!string.IsNullOrWhiteSpace(body.Model) && state.AvailableModels.Contains(body.Model))
    {
        deployment = body.Model;
    }
    else
    {
        var sessionModel = await memoryService.GetSessionModelAsync(agentSessionKey);
        deployment = state.AvailableModels.Contains(sessionModel) ? sessionModel : state.DefaultModel;
    }

    httpContext.Response.Headers.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Append("X-Accel-Buffering", "no");
    httpContext.Response.Headers.Append("Connection", "keep-alive");

    var jsonOpts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Buckets for the final conversation persistence — written once on
    // DoneEvent so the row in `messages` matches what /query would have
    // saved.
    var fullAnswer = new System.Text.StringBuilder();
    var doneSources = new List<string>();
    string? finalTraceId = null;
    string? finalSpanId = null;
    var artifacts = new List<JsonElement>();

    // Tool-call / pipeline-step reasoning, accumulated as StepEvent/
    // ToolCallStartEvent/ToolCallEndEvent arrive so it can be persisted
    // alongside the answer — mirrors the UI's own upsertStep merge pattern
    // (Chat.tsx) so a reloaded conversation renders identical chips to the
    // ones the user saw live.
    var stepRecords = new List<StoredStep>();
    var stepIndexById = new Dictionary<string, int>();
    void UpsertStep(StoredStep step)
    {
        if (stepIndexById.TryGetValue(step.Id, out var idx)) stepRecords[idx] = step;
        else { stepIndexById[step.Id] = stepRecords.Count; stepRecords.Add(step); }
    }

    try
    {
        await foreach (var evt in agentService.RunAgentStreamingAsync(
            query: body.Query,
            sessionId: agentSessionKey,
            deployment: deployment,
            rumSessionId: rumSessionId,
            attachments: attachments,
            ct: httpContext.RequestAborted))
        {
            // Accumulate side-effects we need post-stream.
            switch (evt)
            {
                case TextChunkEvent t: fullAnswer.Append(t.Chunk); break;
                case StepEvent se:
                    UpsertStep(new StoredStep(
                        Kind: "internal", Id: $"internal:{se.Step}", Name: se.Step, Status: se.Status,
                        ArgsJson: null, ResultSummary: null, Sources: null, DurationMs: null, Detail: se.Detail));
                    break;
                case ToolCallStartEvent tcs:
                    UpsertStep(new StoredStep(
                        Kind: "tool", Id: tcs.Id, Name: tcs.Name, Status: "running",
                        ArgsJson: tcs.ArgsJson, ResultSummary: null, Sources: null, DurationMs: null, Detail: null));
                    break;
                case ToolCallEndEvent tce:
                    // Preserve ArgsJson captured at tool_call_start rather than
                    // dropping it — the end event doesn't carry args.
                    var priorArgsJson = stepIndexById.TryGetValue(tce.Id, out var priorIdx)
                        ? stepRecords[priorIdx].ArgsJson
                        : null;
                    UpsertStep(new StoredStep(
                        Kind: "tool", Id: tce.Id, Name: tce.Name, Status: tce.Status,
                        ArgsJson: priorArgsJson, ResultSummary: tce.ResultSummary, Sources: tce.Sources,
                        DurationMs: tce.DurationMs, Detail: null));
                    break;
                case ArtifactEvent ae:
                    artifacts.Add(ae.Artifact.Clone());
                    break;
                case DoneEvent d:
                    doneSources.AddRange(d.Sources);
                    finalTraceId = d.TraceId;
                    finalSpanId = d.SpanId;
                    break;
            }

            // Serialize without the EventName field (it goes on the SSE
            // "event:" line, not in the data payload).
            var payload = JsonSerializer.Serialize((object)evt, evt.GetType(), jsonOpts);
            await httpContext.Response.WriteAsync(
                $"event: {evt.EventName}\ndata: {payload}\n\n",
                httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }
    }
    catch (Exception) when (httpContext.RequestAborted.IsCancellationRequested)
    {
        // Client disconnected mid-stream (tab closed, proxy timeout, network
        // drop). AgentService already persisted the agent session up to this
        // point (see AgentService.RunAgentStreamingAsync); still persist
        // whatever we accumulated here too, so the conversation history the
        // UI reloads isn't silently missing this turn.
    }

    await memoryService.SetSessionModelAsync(agentSessionKey, deployment);

    if (!string.IsNullOrWhiteSpace(conversationId))
    {
        await conversationSvc.SaveMessagesAsync(
            conversationId, userId, body.Query, fullAnswer.ToString(),
            doneSources, finalTraceId, finalSpanId, stepRecords, attachments, artifacts);
    }

    return Results.Empty;
}).RequireAuthorization().RequireRateLimiting("query");

app.MapPost("/suggestions", async (
    SuggestionsRequest body,
    SuggestionService suggestionService,
    AppState state) =>
{
    if (!state.LlmConnected)
        return Results.Ok(new SuggestionsResponse(SuggestionService.FallbackSuggestions));

    var suggestions = await suggestionService.GetContextualSuggestionsAsync(
        body.Query, body.Answer, body.Sources ?? new List<string>());
    return Results.Ok(new SuggestionsResponse(suggestions));
}).RequireAuthorization();

app.MapGet("/suggestions/initial", async (
    SuggestionService suggestionService,
    AppState state) =>
{
    var picked = await suggestionService.GetRandomFromPoolAsync(4);
    if (picked.Count > 0)
    {
        var poolSize = await suggestionService.GetPoolSizeAsync();
        if (poolSize < 20 && state.LlmConnected)
            _ = Task.Run(() => suggestionService.FillPoolAsync());
        return Results.Ok(new SuggestionsResponse(picked));
    }

    if (!state.LlmConnected)
        return Results.Ok(new SuggestionsResponse(SuggestionService.FallbackSuggestions));

    try
    {
        await suggestionService.FillPoolAsync();
        var fresh = await suggestionService.GetRandomFromPoolAsync(4);
        if (fresh.Count > 0) return Results.Ok(new SuggestionsResponse(fresh));
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Initial suggestions fallback LLM call failed error_type={ErrorType}", ex.GetType().Name);
    }

    return Results.Ok(new SuggestionsResponse(SuggestionService.FallbackSuggestions));
}).RequireAuthorization();

app.MapGet("/models", (AppState state) =>
    Results.Ok(new { models = state.AvailableModels, @default = state.DefaultModel }));

// ── /media/upload — chat attachment (image/audio) upload ─────────────────────
// Mirrors Python agent-api's POST /media/upload (media.py) — same allowlist,
// size cap, blob-naming convention, and AZURE_STORAGE_* env vars, so both
// backends can share the chat-media Blob Storage container. This lets the
// .NET pipeline run end-to-end with no dependency on the Python service.
app.MapPost("/media/upload", async (
    HttpContext httpContext,
    MediaService mediaService) =>
{
    var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
    var file = form.Files["file"];
    if (file is null)
        return Results.Problem(detail: "Missing 'file' in form data", statusCode: 422);

    var sessionId = httpContext.Request.Headers["X-Session-ID"].FirstOrDefault()
        ?? Guid.NewGuid().ToString();

    try
    {
        await using var stream = file.OpenReadStream();
        var attachment = await mediaService.UploadAsync(
            fileStream: stream,
            contentLength: file.Length,
            filename: file.FileName,
            contentType: file.ContentType ?? "application/octet-stream",
            sessionId: sessionId,
            ct: httpContext.RequestAborted);
        return Results.Ok(attachment);
    }
    catch (UnsupportedMediaTypeException)
    {
        return Results.Problem(detail: "Unsupported media type", statusCode: 415);
    }
    catch (MediaTooLargeException)
    {
        return Results.Problem(detail: "Attachment exceeds the 10 MB limit", statusCode: 413);
    }
}).RequireAuthorization().RequireRateLimiting("media");

app.MapGet("/tools", async (McpClientHolder holder, AppState state, HttpContext httpContext) =>
{
    if (!state.McpConnected)
        return Results.Problem(detail: "MCP client not available", statusCode: 503);

    var tools = await holder.GetToolsAsync(httpContext.RequestAborted);
    var result = tools.Select(t => new
    {
        name = t.Name,
        description = t.Description,
        // JSON Schema for the tool's input — parity with Python agent-api's
        // GET /tools `parameters` field (main.py), used by the Sandbox tab
        // to show real per-tool schemas instead of hand-maintained examples.
        parameters = (t as AIFunctionDeclaration)?.JsonSchema,
    });
    return Results.Ok(result);
}).RequireAuthorization();

// ── /tools/{name} — invoke a single MCP tool directly, outside the normal
// agent tool-calling loop. Backs the Sandbox tab's "run this tool" button;
// mirrors Python agent-api's POST /tools/{tool_name} (main.py) response
// shape ({tool_name, result, duration_ms} / {tool_name, error, duration_ms})
// so the UI doesn't need backend-specific handling.
app.MapPost("/tools/{name}", async (
    string name,
    HttpContext httpContext,
    McpClientHolder holder,
    AppState state) =>
{
    if (!state.McpConnected)
        return Results.Problem(detail: "MCP client not available", statusCode: 503);

    var tools = await holder.GetToolsAsync(httpContext.RequestAborted);
    var tool = tools.OfType<McpClientTool>().FirstOrDefault(t => t.Name == name);
    if (tool is null)
        return Results.NotFound(new { error = $"Tool '{name}' not found" });

    Dictionary<string, object?> args;
    try
    {
        using var doc = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: httpContext.RequestAborted);
        args = doc.RootElement.ValueKind == JsonValueKind.Object
            ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
            : new Dictionary<string, object?>();
    }
    catch (JsonException)
    {
        args = new Dictionary<string, object?>();
    }

    var sw = Stopwatch.StartNew();
    try
    {
        var callResult = await tool.CallAsync(args, cancellationToken: httpContext.RequestAborted);
        sw.Stop();
        var text = string.Join("\n", callResult.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text));
        var traceId = GetDdTraceId(httpContext, Activity.Current);
        var spanId = GetDdSpanId(Activity.Current);
        if (callResult.IsError == true)
            return Results.Ok(new { tool_name = name, error = "Tool invocation failed", error_type = "McpToolError", duration_ms = sw.Elapsed.TotalMilliseconds, trace_id = traceId, span_id = spanId });
        return Results.Ok(new
        {
            tool_name = name,
            result = (object?)callResult.StructuredContent ?? text,
            duration_ms = sw.Elapsed.TotalMilliseconds,
            // Lets the Sandbox UI link straight to this call's trace in
            // Datadog APM. Note: unlike the Python MCP server, mcp-server-dotnet's
            // tool implementations don't yet log downstream API response
            // bodies on failure (log_external_api_failure only exists on the
            // Python side) — the trace will show latency/status but not the
            // raw response text for now.
            trace_id = traceId,
            span_id = spanId,
        });
    }
    catch (McpException ex)
    {
        sw.Stop();
        return Results.Ok(new
        {
            tool_name = name,
            error = "Tool invocation failed",
            error_type = ex.GetType().Name,
            duration_ms = sw.Elapsed.TotalMilliseconds,
            trace_id = GetDdTraceId(httpContext, Activity.Current),
            span_id = GetDdSpanId(Activity.Current),
        });
    }
}).RequireAuthorization();

// ── /prompts/status — read-only diagnostics for the admin UI ──────────────────
// Mirrors agent-api's GET /admin/prompts/status shape ({prompt_id, backend,
// version, source, flag_value}) so the UI's prompt-versions panel can render
// both backends' rows in one table. Read-only — see PromptHolder.
app.MapGet("/prompts/status", async (PromptHolder holder, PromptVersionFlags flags) =>
{
    var current = holder.Current;
    var flagValue = await flags.ResolveVersionAsync(PromptHolder.PromptId);
    return Results.Ok(new[]
    {
        new
        {
            prompt_id = PromptHolder.PromptId,
            backend = "dotnet",
            version = current.Version,
            source = current.Source,
            flag_value = flagValue,
        },
    });
});

// ── /eval/status — read-only diagnostics for the admin UI ─────────────────────
// Exposes the running eval pipeline state so admins can answer: "is the eval
// pipeline actually firing? at what sample rate? which evaluators are
// registered? are submissions reaching Datadog? what's the recent failure
// rate?" without grepping pod logs or hitting DD's UI.
//
// Read-only by design — mutating sample rate / toggling evaluators at runtime
// would require an audit story we haven't designed yet.
app.MapGet("/eval/status", (
    IEnumerable<InfraAdvisor.AgentApi.Services.Evaluators.IResponseEvaluator> evaluators,
    DatadogEvalsClient ddEvals,
    EvalSubmissionLog log) =>
{
    var snapshot = log.Snapshot();
    var sampleRate = double.TryParse(
        Environment.GetEnvironmentVariable("EVAL_SAMPLE_RATE"),
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out var r) ? Math.Clamp(r, 0.0, 1.0) : 0.1;

    return Results.Ok(new
    {
        sample_rate = sampleRate,
        eval_pipeline = new
        {
            registered_evaluators = evaluators.Select(e => new
            {
                label = e.Label,
                type_name = e.GetType().Name,
                is_llm_judge = e.GetType().Name.StartsWith("Meai", StringComparison.Ordinal),
            }).ToList(),
        },
        datadog = new
        {
            enabled = ddEvals.Enabled,
            ml_app = ddEvals.MlApp,
            site = ddEvals.Site,
            api_key_configured = ddEvals.Enabled,
        },
        judge = new
        {
            deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-5.4-mini",
            note = "M.E.AI Quality evaluator prompts tuned best for GPT-4o-class models. " +
                   "Scores from this deployment are useful as trend signal; " +
                   "absolute thresholds need recalibration.",
        },
        submissions = new
        {
            total = snapshot.TotalSubmitted,
            failed = snapshot.TotalFailed,
            success_rate = snapshot.TotalSubmitted == 0
                ? (double?)null
                : Math.Round(1.0 - (double)snapshot.TotalFailed / snapshot.TotalSubmitted, 3),
            recent = snapshot.Recent.Select(e => new
            {
                timestamp_iso = e.Timestamp.ToString("o"),
                label = e.Label,
                metric_type = e.MetricType,
                value = e.Value,
                success = e.Success,
                duration_ms = e.DurationMs,
                trace_id_decimal = e.TraceIdDecimal,
                span_id_decimal = e.SpanIdDecimal,
                reasoning = e.Reasoning,
                error = e.Error,
            }).ToList(),
        },
    });
});

// ── /ai-guard/status — read-only diagnostics for the admin UI ─────────────────
// AI Guard's HTTP API path (the only option for MAF — no LangChain-equivalent
// auto-integration exists) sends no traces to Datadog. This endpoint is the
// only in-app visibility into whether AI Guard is actually evaluating
// requests, what it's deciding, and whether calls are succeeding.
app.MapGet("/ai-guard/status", (
    DatadogAiGuardClient aiGuard,
    AiGuardSubmissionLog log) =>
{
    var snapshot = log.Snapshot();
    return Results.Ok(new
    {
        datadog = new
        {
            enabled = aiGuard.Enabled,
            note = "AI Guard HTTP API path sends no traces to Datadog — this panel is the " +
                   "only in-app visibility. See llm-engineering/ai-guard.mdx.",
        },
        evaluations = new
        {
            total = snapshot.TotalEvaluated,
            blocked = snapshot.TotalBlocked,
            failed = snapshot.TotalFailed,
            recent = snapshot.Recent.Select(e => new
            {
                timestamp_iso = e.Timestamp.ToString("o"),
                action = e.Action,
                reason = e.Reason,
                success = e.Success,
                duration_ms = e.DurationMs,
                trace_id_decimal = e.TraceIdDecimal,
                span_id_decimal = e.SpanIdDecimal,
                error = e.Error,
            }).ToList(),
        },
    });
});

app.MapPost("/feedback", async (FeedbackRequest body, HttpContext httpContext, DatadogEvalsClient ddEvals, CancellationToken cancellationToken) =>
{
    var validRatings = new HashSet<string> { "positive", "negative", "reported" };
    if (!validRatings.Contains(body.Rating))
    {
        return Results.Problem(
            detail: $"Invalid rating '{body.Rating}'. Must be one of: {string.Join(", ", validRatings.Order())}",
            statusCode: 422);
    }

    var submitterId = httpContext.User.FindFirst("sub")?.Value;
    if (submitterId is null) return Results.Unauthorized();

    // Keep low-cardinality APM tags and a counter for API operations, then send
    // the separate LLM Observability feedback event against the response span.
    var current = Activity.Current;
    foreach (var tag in TelemetryPrivacy.SafeFeedbackTags(body.TraceId, body.SpanId, body.Rating, body.SessionId))
        current?.SetTag(tag.Key, tag.Value);

    feedbackCounter.Add(1, new KeyValuePair<string, object?>("rating", body.Rating));
    var submitted = await ddEvals.SubmitFeedbackAsync(body.TraceId, body.SpanId, body.Rating, submitterId, cancellationToken);
    if (!submitted) return Results.Problem(detail: "Feedback could not be submitted", statusCode: 502);

    return Results.StatusCode(204);
}).RequireAuthorization();

app.MapGet("/health", (AppState state) =>
    Results.Ok(new
    {
        status = "ok",
        service = "infra-advisor-agent-api-dotnet",
        mcp_connected = state.McpConnected,
        llm_connected = state.LlmConnected,
    }));

app.MapGet("/livez", () => Results.Ok(new { status = "ok", service = "infra-advisor-agent-api-dotnet" }));
app.MapGet("/readyz", (AppState state) => state.McpConnected && state.LlmConnected
    ? Results.Ok(new { status = "ready", service = "infra-advisor-agent-api-dotnet", mcp_connected = state.McpConnected, llm_connected = state.LlmConnected })
    : Results.Json(new { status = "not_ready", service = "infra-advisor-agent-api-dotnet", mcp_connected = state.McpConnected, llm_connected = state.LlmConnected }, statusCode: 503));

app.MapDelete("/session/{sessionId}", async (string sessionId, HttpContext httpContext, MemoryService memoryService) =>
{
    var userId = SubClaim(httpContext);
    if (userId is null) return Results.Unauthorized();
    var cleared = await memoryService.ClearSessionAsync(TenantSessionKey.Create(userId, sessionId));
    return Results.Ok(new { session_id = sessionId, cleared = cleared });
}).RequireAuthorization();

// ── Conversations ─────────────────────────────────────────────────────────────
// User identity is the JWT `sub` claim — the previous X-User-ID header was
// spoofable. UseAuthentication() above populates HttpContext.User; the
// RequireAuthorization() suffix below guarantees it's non-null.

static string? SubClaim(HttpContext ctx) =>
    ctx.User?.FindFirst("sub")?.Value;

app.MapPost("/conversations", async (HttpContext httpContext, ConversationService conversationSvc) =>
{
    var userId = SubClaim(httpContext);
    if (userId is null) return Results.Unauthorized();

    string? title = null, model = null, backend = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: httpContext.RequestAborted);
        if (doc.RootElement.TryGetProperty("title", out var t)) title = t.GetString();
        if (doc.RootElement.TryGetProperty("model", out var m)) model = m.GetString();
        if (doc.RootElement.TryGetProperty("backend", out var b)) backend = b.GetString();
    }
    catch { }

    try
    {
        var conv = await conversationSvc.CreateConversationAsync(
            userId, title ?? "New Conversation", model, backend ?? "dotnet");
        return conv is null
            ? Results.Problem(detail: "Conversation persistence not available", statusCode: 503)
            : Results.Ok(conv);
    }
    catch (Exception ex)
    {
        var publicError = PublicError.Unexpected(ex, "Unable to create conversation");
        app.Logger.LogWarning("Conversation creation failed error_type={ErrorType}", ex.GetType().Name);
        return Results.Problem(detail: publicError.Detail, statusCode: 500,
            extensions: new Dictionary<string, object?> { ["error_type"] = publicError.ErrorType, ["trace_id"] = GetDdTraceId(httpContext, Activity.Current) });
    }
}).RequireAuthorization();

app.MapGet("/conversations", async (HttpContext httpContext, ConversationService conversationSvc) =>
{
    var userId = SubClaim(httpContext);
    if (userId is null) return Results.Unauthorized();
    var list = await conversationSvc.ListConversationsAsync(userId);
    return Results.Ok(list);
}).RequireAuthorization();

app.MapGet("/conversations/{id}", async (string id, HttpContext httpContext, ConversationService conversationSvc) =>
{
    var userId = SubClaim(httpContext);
    if (userId is null) return Results.Unauthorized();
    var conv = await conversationSvc.GetConversationAsync(id, userId);
    return conv is null ? Results.NotFound() : Results.Ok(conv);
}).RequireAuthorization();

app.MapDelete("/conversations/{id}", async (string id, HttpContext httpContext, ConversationService conversationSvc) =>
{
    var userId = SubClaim(httpContext);
    if (userId is null) return Results.Unauthorized();
    var deleted = await conversationSvc.DeleteConversationAsync(id, userId);
    return deleted ? Results.StatusCode(204) : Results.NotFound();
}).RequireAuthorization();

app.Run();
