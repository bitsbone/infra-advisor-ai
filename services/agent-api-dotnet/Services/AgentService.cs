using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using InfraAdvisor.AgentApi.Models;
using InfraAdvisor.AgentApi.Services.Evaluators;
using StreamEvent = InfraAdvisor.AgentApi.Models.StreamEvent;

namespace InfraAdvisor.AgentApi.Services;

// Agent orchestrator backed by Microsoft Agents Framework.
//
// Replaces ~500 lines of hand-rolled router→specialist→tool-loop code with
// the MAF builder pipeline. The single ChatClientAgent has access to every
// MCP tool exposed by mcp-server-dotnet; the model picks which to call.
// MAF's .UseOpenTelemetry() emits the invoke_agent span; M.E.AI's
// .UseOpenTelemetry() on the chat client (set up in Program.cs) emits the
// chat + execute_tool spans inside it.
//
// Session memory + persistence is handled by AgentSessionStore (Redis JSON
// round-trip via SerializeSessionAsync / DeserializeSessionAsync), not here.
public class AgentService
{
    private readonly AgentHolder _agentHolder;
    private readonly McpClientHolder _mcpHolder;
    private readonly AgentSessionStore _sessions;
    private readonly RetrievalService _retrieval;
    private readonly IReadOnlyList<IResponseEvaluator> _evaluators;
    private readonly DatadogEvalsClient _ddEvals;
    private readonly DatadogAiGuardClient _aiGuard;
    // Null when AZURE_OPENAI_WHISPER_ENDPOINT/AZURE_OPENAI_WHISPER_API_KEY
    // aren't both set (Program.cs only registers the keyed "whisper" client
    // when they are) — voice attachment transcription degrades to "skipped"
    // rather than failing startup, since it's an additive feature.
    private readonly AzureOpenAIClient? _whisperOpenAiClient;
    private readonly HttpClient _mediaHttpClient;
    private readonly string _whisperDeployment;
    private readonly Histogram<double> _faithfulnessHistogram;
    private readonly Counter<long> _conversationCounter;
    private readonly Counter<long> _toolCounter;
    private readonly Counter<long> _mcpReconnectCounter;
    private readonly double _evalSampleRate;
    private readonly ILogger<AgentService> _logger;

    // ActivitySource for manual spans that the M.E.AI / MAF decorators don't
    // emit on their own — task (classify_domain) here, retrieval inside
    // RetrievalService. Same source name TelemetrySetup AddSource's so they
    // get exported.
    private static readonly ActivitySource ActivitySource =
        new(Observability.TelemetrySetup.ActivitySourceName);

    public AgentService(
        AgentHolder agentHolder,
        McpClientHolder mcpHolder,
        AgentSessionStore sessions,
        RetrievalService retrieval,
        IEnumerable<IResponseEvaluator> evaluators,
        DatadogEvalsClient ddEvals,
        DatadogAiGuardClient aiGuard,
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        IMeterFactory meterFactory,
        ILogger<AgentService> logger)
    {
        _agentHolder = agentHolder;
        _mcpHolder = mcpHolder;
        _sessions = sessions;
        _retrieval = retrieval;
        _evaluators = evaluators.ToList();
        _ddEvals = ddEvals;
        _aiGuard = aiGuard;
        // GetKeyedService (not GetRequiredKeyedService) so a missing
        // registration resolves to null instead of throwing at startup —
        // Program.cs only registers this key when both Whisper env vars
        // are present.
        _whisperOpenAiClient = serviceProvider.GetKeyedService<AzureOpenAIClient>("whisper");
        _mediaHttpClient = httpClientFactory.CreateClient("agent-media-download");
        _whisperDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_WHISPER_DEPLOYMENT") ?? "whisper";
        _logger = logger;
        _evalSampleRate = double.TryParse(
            Environment.GetEnvironmentVariable("EVAL_SAMPLE_RATE"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var r) ? Math.Clamp(r, 0.0, 1.0) : 0.1;

        var meter = meterFactory.Create(Observability.TelemetrySetup.ActivitySourceName);
        _faithfulnessHistogram = meter.CreateHistogram<double>(
            "agent.faithfulness_score",
            description: "Faithfulness evaluation score for agent responses");
        _conversationCounter = meter.CreateCounter<long>(
            "infra_advisor.conversation.completed",
            description: "Count of completed /query calls. Tagged with query.domain.");
        _toolCounter = meter.CreateCounter<long>(
            "infra_advisor.tool.invoked",
            description: "Count of MCP tool invocations made by the agent. Tagged with tool.name + query.domain.");
        _mcpReconnectCounter = meter.CreateCounter<long>(
            "infra_advisor.mcp.reconnect",
            description: "Count of MCP client reconnects triggered by session-expired errors. Tagged with reason.");
    }

    public async Task<AgentResult> RunAgentAsync(
        string query,
        string sessionId,
        string deployment,
        List<AttachmentDto>? attachments = null,
        CancellationToken ct = default)
    {
        // 0. AI Guard pre-flight check on the raw user query. Runs before
        //    anything else touches the LLM/tool loop — see DatadogAiGuardClient
        //    for why this is the HTTP API path (no LangChain-equivalent
        //    auto-integration exists for Microsoft Agent Framework) and why
        //    it fails open on transport errors.
        var guardResult = await _aiGuard.EvaluateAsync(
            new[] { new AiGuardMessage("user", query) }, ct);
        if (guardResult.IsBlocked)
        {
            _logger.LogWarning(
                "AI Guard blocked query for session={SessionId}: {Action} — {Reason}",
                sessionId, guardResult.Action, guardResult.Reason);
            return new AgentResult(
                Answer: "",
                Sources: new List<string>(),
                ToolsCalled: new List<string>(),
                QueryDomain: "blocked",
                Blocked: true,
                BlockReason: guardResult.Reason ?? $"Blocked by AI Guard ({guardResult.Action})");
        }

        // 1. Fold any audio attachment's transcript into the query text
        //    (cascade architecture — the chat LLM never sees raw audio) and
        //    surface the image attachment, if any, before anything else
        //    touches the query text — so domain classification + retrieval
        //    both see the transcript.
        var (effectiveQuery, imageAttachment) = await BuildEffectiveQueryAsync(query, attachments, ct);

        // 2. Task: classify the query domain (manual span — pure CS, no LLM).
        var domain = ClassifyDomainTraced(effectiveQuery);

        // 3. Retrieval: vector-search the best-practices corpus. Emits a
        //    retrieval span which wraps a framework-emitted embedding span
        //    (query embedding). Failures degrade silently — the agent still
        //    answers without retrieved context.
        var retrieved = await _retrieval.RetrieveAsync(effectiveQuery, topK: 3, ct);

        // 4. Inject retrieval context as a system-style preamble. Cheap and
        //    keeps the agent prompt unchanged structurally.
        var augmentedQuery = retrieved.Count > 0
            ? $"Relevant InfraAdvisor best-practice context:\n{string.Join("\n\n", retrieved)}\n\n---\n\nUser question: {effectiveQuery}"
            : effectiveQuery;

        // Multi-part vision message when an image attachment is present —
        // TextContent + UriContent, same shape RunAsync accepts for plain
        // text via a single TextContent part.
        var inputMessage = BuildAgentInputMessage(augmentedQuery, imageAttachment);

        var agent = await _agentHolder.GetAgentAsync(ct);

        // Session lookup / restore / save round-trip wraps the MAF agent call.
        var session = await _sessions.GetOrCreateAsync(agent, sessionId, ct);

        AgentResponse response;
        try
        {
            response = await agent.RunAsync(inputMessage, session, cancellationToken: ct);
        }
        catch (Exception ex) when (IsMcpSessionExpired(ex))
        {
            // mcp-server-dotnet was restarted while this agent-api pod was
            // up — the cached McpClient's session ID no longer maps to
            // anything on the (new) server. Reconnect, rebuild the agent
            // with the fresh tool list, recreate the agent session (the
            // old one was tied to the old agent's context), and retry once.
            _logger.LogWarning(
                "MCP session expired for session={SessionId} — reconnecting and retrying once: {Error}",
                sessionId, RootErrorMessage(ex));
            _mcpReconnectCounter.Add(1,
                new KeyValuePair<string, object?>("reason", "session_expired"));
            await _mcpHolder.RefreshAsync(ct);
            agent = await _agentHolder.GetAgentAsync(ct);
            // Restored session JSON references AITool instances that came
            // from the prior MCP client; recreate fresh against the new
            // agent so the tool call routing wires correctly.
            session = await agent.CreateSessionAsync(ct);
            response = await agent.RunAsync(inputMessage, session, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("agent.RunAsync failed for session={SessionId}: {Error}",
                sessionId, ex.Message);
            throw;
        }

        await _sessions.SaveAsync(agent, sessionId, session, ct);

        var answer = response.Text ?? "";
        var sources = ExtractSourcesFromResponse(response);
        var toolsCalled = ExtractToolsCalledFromResponse(response);

        // Business metrics — increment once per completed query plus once
        // per MCP tool invocation. Tags let dashboards slice by domain and
        // tool name without further code changes.
        var domainTag = new KeyValuePair<string, object?>("query.domain", domain);
        _conversationCounter.Add(1, domainTag);
        foreach (var tool in toolsCalled)
            _toolCounter.Add(1,
                new KeyValuePair<string, object?>("tool.name", tool),
                domainTag);

        // External evaluations — fire-and-forget so /query latency is
        // unchanged. Captured AgentSpanContext lets us address the agent
        // span specifically (not the HTTP root) when joining to DD's
        // eval-metric API.
        var toolResults = ExtractToolResultsFromResponse(response);
        ScheduleEvaluations(effectiveQuery, answer, toolsCalled, toolResults, sources, domain);

        return new AgentResult(
            Answer: answer,
            Sources: sources,
            ToolsCalled: toolsCalled,
            QueryDomain: domain);
    }

    private void ScheduleEvaluations(
        string query, string answer,
        List<string> toolsCalled, List<string> toolResults,
        List<string> sources, string domain)
    {
        if (_evalSampleRate <= 0 || _evaluators.Count == 0) return;
        if (Random.Shared.NextDouble() >= _evalSampleRate) return;

        var captured = AgentSpanContext.Current;
        if (captured is null)
        {
            _logger.LogDebug("Skipping eval: AgentSpanContext not captured (no invoke_agent span on this request?)");
            return;
        }

        var input = new EvalInput(query, answer, toolsCalled, toolResults, sources, domain);
        var evaluators = _evaluators;
        var client = _ddEvals;
        var promptVersionTag = $"prompt.version:{Environment.GetEnvironmentVariable("PROMPT_VERSION") ?? "v1"}";

        _ = Task.Run(async () =>
        {
            foreach (var ev in evaluators)
            {
                try
                {
                    var result = await ev.EvaluateAsync(input, CancellationToken.None);
                    var extraTags = new[]
                    {
                        $"query.domain:{domain}",
                        promptVersionTag,
                    };
                    await DispatchAsync(client, captured, ev.Label, result, extraTags);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Evaluator {Label} threw: {Error}", ev.Label, ex.Message);
                }
            }
        });
    }

    private static Task DispatchAsync(
        DatadogEvalsClient client, AgentSpanContext.Captured captured,
        string label, EvalResult result, IEnumerable<string> extraTags) =>
        result.MetricType switch
        {
            "boolean" => client.SubmitBooleanAsync(
                captured.TraceIdDecimal, captured.SpanIdDecimal,
                label, (bool)result.Value, result.Reasoning, extraTags),
            "score"   => client.SubmitScoreAsync(
                captured.TraceIdDecimal, captured.SpanIdDecimal,
                label, Convert.ToDouble(result.Value), result.Reasoning, extraTags),
            "categorical" => client.SubmitCategoricalAsync(
                captured.TraceIdDecimal, captured.SpanIdDecimal,
                label, result.Value.ToString() ?? "", result.Reasoning, extraTags),
            _ => Task.CompletedTask,
        };

    // Detect an MCP session-expired condition anywhere in the exception
    // chain. mcp-server-dotnet returns HTTP 404 with "session has expired"
    // when the Mcp-Session-Id the client holds no longer maps to a live
    // session on the (post-restart) server. The .NET MCP client surfaces
    // this as ClientTransportClosedException whose message includes the
    // hint phrase. We also accept any McpException whose message names a
    // session issue, in case the SDK wraps differently in future versions.
    private static bool IsMcpSessionExpired(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is ClientTransportClosedException) return true;
            if (e is McpException && e.Message.Contains("session", StringComparison.OrdinalIgnoreCase))
                return true;
            // Bare HTTP 404 on the MCP transport — older SDK paths surfaced
            // it without wrapping in ClientTransportClosedException.
            if (e is HttpRequestException hex &&
                (int?)hex.StatusCode == 404 &&
                e.Message.Contains("/mcp", StringComparison.OrdinalIgnoreCase))
                return true;
            if (e.InnerException is null) break;
        }
        return false;
    }

    private static string RootErrorMessage(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null) e = e.InnerException;
        return e.Message;
    }

    // Turns a mid-stream exception into a clear, user-actionable message +
    // machine-readable category for the terminal ErrorEvent. Never surfaces
    // raw .NET/HTTP exception text to the user — the full exception is still
    // logged server-side at the yield site for diagnosis.
    private static (string Message, string Category) ClassifyStreamError(
        Exception ex, bool sessionRetryAttempted)
    {
        if (IsMcpSessionExpired(ex))
        {
            return sessionRetryAttempted
                ? ("The infrastructure data service restarted and reconnecting failed. Please retry your question.", "mcp_session_expired")
                : ("The infrastructure data service restarted. Please retry your question.", "mcp_session_expired");
        }
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is TaskCanceledException or OperationCanceledException or TimeoutException)
                return ("A backend service didn't respond in time. Please retry your question.", "upstream_timeout");
            if (e.InnerException is null) break;
        }
        return (ex.Message, "unknown");
    }

    // Wraps ClassifyDomain in a manual Activity tagged so DD LLMObs renders
    // it as a "task" kind span (alongside the agent / chat / tool / embedding
    // / retrieval kinds emitted elsewhere in this trace).
    private static string ClassifyDomainTraced(string query)
    {
        using var activity = ActivitySource.StartActivity("classify_domain", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "classify_domain");
        activity?.SetTag("dd.llmobs.span.kind", "task");
        activity?.SetTag("input.value", query);

        var domain = ClassifyDomain(query);

        activity?.SetTag("output.value", domain);
        activity?.SetTag("query.domain", domain);
        return domain;
    }

    // ── Multimodal attachment handling ────────────────────────────────────────
    // Cascade architecture (mirrors services/agent-api/src/agent.py): audio is
    // never sent to the chat LLM — it's transcribed here via Azure OpenAI
    // Whisper and the transcript text is folded into the effective query.
    // Images become a UriContent part on the ChatMessage sent to the agent.

    private async Task<(string EffectiveQuery, AttachmentDto? ImageAttachment)> BuildEffectiveQueryAsync(
        string query, List<AttachmentDto>? attachments, CancellationToken ct)
    {
        if (attachments is null || attachments.Count == 0)
            return (query, null);

        var imageAttachment = attachments.FirstOrDefault(a => a.Kind == "image");
        var audioAttachment = attachments.FirstOrDefault(a => a.Kind == "audio");

        if (audioAttachment is null)
            return (query, imageAttachment);

        var transcript = await TranscribeAudioIfPresentAsync(audioAttachment, ct);
        if (string.IsNullOrEmpty(transcript))
            return (query, imageAttachment);

        var effectiveQuery = string.IsNullOrEmpty(query)
            ? transcript
            : $"{query}\n\n[Transcribed voice message]: {transcript}";
        return (effectiveQuery, imageAttachment);
    }

    // Own Activity (not the framework-emitted chat span) — Whisper isn't
    // called through the M.E.AI IChatClient pipeline, so nothing else would
    // emit a span for it.
    //
    // gen_ai.input.messages / gen_ai.output.messages follow the OTel GenAI
    // semantic conventions (v1.37+) message-parts schema — the SAME
    // attributes Microsoft.Extensions.AI's own .UseOpenTelemetry() decorator
    // emits automatically for chat calls (confirmed empirically: a UriContent
    // image part on a ChatMessage already serializes as
    // {"type":"uri","uri":...,"mime_type":...,"modality":"image"} with no
    // extra code — that's why images need zero changes here). Audio has no
    // such automatic path since this call goes through Azure.AI.OpenAI's
    // AudioClient directly, not an IChatClient, so it's built by hand here,
    // matching the same "uri" part shape with modality "audio" instead of
    // inline base64 — the audio blob already has a stable SAS URL, so
    // referencing it avoids embedding a potentially large base64 payload in
    // a span attribute. dd.llmobs.span.kind=llm (not "task") since this is a
    // real model call (Whisper), same reasoning as the Python side's switch
    // from LLMObs.task to LLMObs.llm for this same step.
    private async Task<string?> TranscribeAudioIfPresentAsync(AttachmentDto audioAttachment, CancellationToken ct)
    {
        if (_whisperOpenAiClient is null)
        {
            _logger.LogWarning(
                "Skipping audio transcription for url={Url} — AZURE_OPENAI_WHISPER_ENDPOINT/AZURE_OPENAI_WHISPER_API_KEY not configured",
                audioAttachment.Url);
            return null;
        }

        using var activity = ActivitySource.StartActivity("transcribe_audio", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "transcribe_audio");
        activity?.SetTag("gen_ai.request.model", _whisperDeployment);
        activity?.SetTag("gen_ai.provider.name", "azure.ai.openai");
        activity?.SetTag("dd.llmobs.span.kind", "llm");

        var inputMessages = JsonSerializer.Serialize(new[]
        {
            new
            {
                role = "user",
                parts = new object[]
                {
                    new { type = "uri", uri = audioAttachment.Url, mime_type = audioAttachment.MimeType, modality = "audio" },
                },
            },
        });
        activity?.SetTag("gen_ai.input.messages", inputMessages);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var audioBytes = await _mediaHttpClient.GetByteArrayAsync(audioAttachment.Url, ct);
            var audioClient = _whisperOpenAiClient.GetAudioClient(_whisperDeployment);
            var ext = audioAttachment.MimeType switch
            {
                "audio/wav" => "wav",
                "audio/mpeg" => "mp3",
                "audio/ogg" => "ogg",
                _ => "webm",
            };
            using var stream = new MemoryStream(audioBytes);
            var result = await audioClient.TranscribeAudioAsync(stream, $"audio.{ext}", cancellationToken: ct);
            stopwatch.Stop();

            var outputMessages = JsonSerializer.Serialize(new[]
            {
                new
                {
                    role = "assistant",
                    parts = new object[] { new { type = "text", content = result.Value.Text } },
                },
            });
            activity?.SetTag("gen_ai.output.messages", outputMessages);
            activity?.SetTag("audio.duration_s", (stopwatch.ElapsedMilliseconds / 1000.0).ToString("F2"));
            return result.Value.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("transcribe_audio failed for url={Url}: {Error}", audioAttachment.Url, ex.Message);
            activity?.SetTag("error", true);
            return null;
        }
    }

    // Plain text ChatMessage, or a multi-part vision message when an image
    // attachment is present.
    private static ChatMessage BuildAgentInputMessage(string text, AttachmentDto? imageAttachment)
    {
        if (imageAttachment is null)
            return new ChatMessage(ChatRole.User, text);

        return new ChatMessage(ChatRole.User, new AIContent[]
        {
            new TextContent(text),
            new UriContent(imageAttachment.Url, imageAttachment.MimeType),
        });
    }

    // Streaming variant of RunAgentAsync. Yields StreamEvent records the
    // /query/stream endpoint serializes as Server-Sent Events. Same pipeline
    // as the non-streaming version (classify → retrieve → agent.RunStreamingAsync
    // → session save → metrics → evals), with two differences:
    //   - tool calls + text chunks surface live as the model emits them
    //   - MCP session-expired retry is NOT attempted mid-stream (text
    //     already streamed to the client can't be cleanly rewound).
    //     The retry path lives in RunAgentAsync; clients can fall back
    //     to /query if the streaming path fails.
    public async IAsyncEnumerable<StreamEvent> RunAgentStreamingAsync(
        string query,
        string sessionId,
        string deployment,
        List<AttachmentDto>? attachments = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[stream] starting for session={SessionId}; ct already cancelled: {AlreadyCancelled}",
            sessionId, ct.IsCancellationRequested);

        // 0. AI Guard pre-flight check — see RunAgentAsync for rationale.
        //    Streaming can't rewind text already sent to the client, so this
        //    must run before the first StreamEvent goes out.
        var guardResult = await _aiGuard.EvaluateAsync(
            new[] { new AiGuardMessage("user", query) }, ct);
        if (guardResult.IsBlocked)
        {
            _logger.LogWarning(
                "AI Guard blocked streaming query for session={SessionId}: {Action} — {Reason}",
                sessionId, guardResult.Action, guardResult.Reason);
            yield return new ErrorEvent(
                guardResult.Reason ?? $"Blocked by AI Guard ({guardResult.Action})",
                TraceId: Activity.Current?.TraceId.ToString());
            yield break;
        }

        // 1. transcribe_audio (if an audio attachment is present) — folds the
        //    transcript into the query text before anything else touches it.
        var hasAudio = attachments?.Any(a => a.Kind == "audio") ?? false;
        if (hasAudio) yield return new StepEvent("transcribe_audio", "running");
        var (effectiveQuery, imageAttachment) = await BuildEffectiveQueryAsync(query, attachments, ct);
        if (hasAudio) yield return new StepEvent("transcribe_audio", "done");

        // 2. classify_domain (sync) — instant; report as a completed step.
        var domain = ClassifyDomainTraced(effectiveQuery);
        yield return new StepEvent("classify_domain", "done", domain);

        // 3. retrieve_best_practices — let the user see it's happening.
        yield return new StepEvent("retrieve_best_practices", "running");
        var retrieved = await _retrieval.RetrieveAsync(effectiveQuery, topK: 3, ct);
        yield return new StepEvent("retrieve_best_practices", "done", $"{retrieved.Count} docs");

        var augmentedQuery = retrieved.Count > 0
            ? $"Relevant InfraAdvisor best-practice context:\n{string.Join("\n\n", retrieved)}\n\n---\n\nUser question: {effectiveQuery}"
            : effectiveQuery;

        var inputMessage = BuildAgentInputMessage(augmentedQuery, imageAttachment);

        var agent = await _agentHolder.GetAgentAsync(ct);
        var session = await _sessions.GetOrCreateAsync(agent, sessionId, ct);

        // Track tool-call lifecycle as the model emits FunctionCallContent
        // / FunctionResultContent updates. Start times are captured at
        // ToolCallStart so the End event reports duration even when MAF
        // emits the call + result back-to-back in a single update batch.
        var toolStarts = new Dictionary<string, (long StartTicks, string Name)>();
        var allSources = new List<string>();
        var toolsCalledOrdered = new List<string>();
        var toolResults = new List<string>();
        var fullAnswer = new System.Text.StringBuilder();
        Exception? streamError = null;

        // Whether anything the user can already see (a tool chip or answer
        // text) has been yielded yet. A session-expired reconnect is only
        // safe to retry transparently while this is still false — once
        // something has streamed, replaying the query would duplicate or
        // contradict what the client already rendered.
        var anyStreamed = false;
        var retriedMcpSession = false;

        var updates = agent.RunStreamingAsync(inputMessage, session, cancellationToken: ct);
        var enumerator = updates.GetAsyncEnumerator(ct);

        while (true)
        {
            // Wrap MoveNextAsync so iterator exceptions become an
            // ErrorEvent we yield — yield-return inside a try/catch is
            // a C# constraint, so we step manually here.
            bool moved;
            try { moved = await enumerator.MoveNextAsync(); }
            catch (Exception ex) when (IsMcpSessionExpired(ex) && !anyStreamed && !retriedMcpSession)
            {
                // mcp-server-dotnet was restarted before any tool call/text
                // reached the client — safe to reconnect and restart the
                // stream from scratch, same recovery RunAgentAsync does for
                // /query. Not attempted once anyStreamed is true (see above).
                retriedMcpSession = true;
                _logger.LogWarning(
                    "MCP session expired mid-stream for session={SessionId} before any output — reconnecting and restarting once: {Error}",
                    sessionId, RootErrorMessage(ex));
                _mcpReconnectCounter.Add(1,
                    new KeyValuePair<string, object?>("reason", "session_expired_stream"));
                await enumerator.DisposeAsync();
                await _mcpHolder.RefreshAsync(ct);
                agent = await _agentHolder.GetAgentAsync(ct);
                // Restored session JSON references AITool instances from the
                // prior MCP client; recreate fresh against the new agent so
                // tool call routing wires correctly (same reasoning as
                // RunAgentAsync's retry path).
                session = await agent.CreateSessionAsync(ct);
                toolStarts.Clear();
                allSources.Clear();
                toolsCalledOrdered.Clear();
                toolResults.Clear();
                fullAnswer.Clear();
                updates = agent.RunStreamingAsync(augmentedQuery, session, cancellationToken: ct);
                enumerator = updates.GetAsyncEnumerator(ct);
                continue;
            }
            catch (Exception ex)
            {
                streamError = ex;
                break;
            }
            if (!moved) break;

            var update = enumerator.Current;
            foreach (var ev in HandleUpdate(update, toolStarts, allSources, toolsCalledOrdered, toolResults, fullAnswer))
            {
                if (ev is ToolCallStartEvent or TextChunkEvent) anyStreamed = true;
                yield return ev;
            }
        }
        await enumerator.DisposeAsync();

        if (streamError is not null)
        {
            var (errorMessage, errorCategory) = ClassifyStreamError(streamError, retriedMcpSession);
            _logger.LogWarning(
                "agent.RunStreamingAsync failed for session={SessionId} category={Category}: {Error}",
                sessionId, errorCategory, RootErrorMessage(streamError));
            yield return new ErrorEvent(
                errorMessage, TraceId: Activity.Current?.TraceId.ToString(), Category: errorCategory);
            yield break;
        }

        await _sessions.SaveAsync(agent, sessionId, session, ct);

        var domainTag = new KeyValuePair<string, object?>("query.domain", domain);
        _conversationCounter.Add(1, domainTag);
        foreach (var tool in toolsCalledOrdered.Distinct())
            _toolCounter.Add(1,
                new KeyValuePair<string, object?>("tool.name", tool),
                domainTag);

        var distinctSources = allSources.Distinct().ToList();
        var distinctTools = toolsCalledOrdered.Distinct().ToList();

        ScheduleEvaluations(effectiveQuery, fullAnswer.ToString(), distinctTools, toolResults, distinctSources, domain);

        yield return new DoneEvent(
            TraceId: GetTraceIdDecimal(),
            SpanId: GetSpanIdDecimal(),
            SessionId: sessionId,
            Model: deployment,
            Sources: distinctSources,
            ToolsCalled: distinctTools,
            QueryDomain: domain);
    }

    // Process one AgentResponseUpdate into zero-or-more StreamEvents. Pure
    // function over the running state buckets — extracted so the iterator
    // method stays scannable.
    private static IEnumerable<StreamEvent> HandleUpdate(
        AgentResponseUpdate update,
        Dictionary<string, (long StartTicks, string Name)> toolStarts,
        List<string> allSources,
        List<string> toolsCalledOrdered,
        List<string> toolResults,
        System.Text.StringBuilder fullAnswer)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent fc:
                    toolStarts[fc.CallId] = (Stopwatch.GetTimestamp(), fc.Name);
                    toolsCalledOrdered.Add(fc.Name);
                    yield return new ToolCallStartEvent(
                        Id: fc.CallId,
                        Name: fc.Name,
                        ArgsJson: fc.Arguments is null ? null : JsonSerializer.Serialize(fc.Arguments));
                    break;

                case FunctionResultContent fr:
                    var sources = new List<string>();
                    var resultStr = fr.Result?.ToString() ?? "";
                    TryExtractSources(resultStr, sources);
                    allSources.AddRange(sources);
                    // Capture the tool result for LLM-judge evaluators
                    // (Groundedness needs the data to check claims against).
                    // Cap at 4KB to keep judge prompts reasonable.
                    toolResults.Add(resultStr.Length > 4000 ? resultStr[..4000] + "…" : resultStr);

                    var (startTicks, name) = toolStarts.TryGetValue(fr.CallId, out var s) ? s : (0L, fr.CallId);
                    var durationMs = startTicks > 0
                        ? (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency
                        : 0.0;

                    yield return new ToolCallEndEvent(
                        Id: fr.CallId,
                        Name: name,
                        Status: fr.Exception is null ? "ok" : "error",
                        ResultSummary: SummarizeToolResult(resultStr),
                        Sources: sources,
                        DurationMs: durationMs);
                    break;

                case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                    fullAnswer.Append(tc.Text);
                    yield return new TextChunkEvent(tc.Text);
                    break;
            }
        }
    }

    // Compact one-liner summary for a tool result. Strategy: try to parse
    // as JSON and report array length / object key count; fall back to
    // a length-bounded character count for plain text.
    private static string? SummarizeToolResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => $"{doc.RootElement.GetArrayLength()} records",
                JsonValueKind.Object when doc.RootElement.TryGetProperty("error", out _) =>
                    "error",
                JsonValueKind.Object => $"{CountObjectKeys(doc.RootElement)} fields",
                _ => null,
            };
        }
        catch
        {
            var len = raw.Length;
            return len < 1024 ? $"{len} chars" : $"{len / 1024} KB";
        }
    }

    private static int CountObjectKeys(JsonElement obj)
    {
        var n = 0;
        foreach (var _ in obj.EnumerateObject()) n++;
        return n;
    }

    // Decimal-encoded trace/span IDs match the rest of our DD plumbing
    // (RUM injection, eval-metric joins). Lower-64-bit-of-128 for trace,
    // raw 64-bit for span — same pattern Program.cs uses on /query.
    private static string? GetTraceIdDecimal()
    {
        var hex = Activity.Current?.TraceId.ToString();
        if (hex is not { Length: 32 }) return hex;
        return ulong.TryParse(hex[16..], System.Globalization.NumberStyles.HexNumber, null, out var lo)
            ? lo.ToString() : hex;
    }

    private static string? GetSpanIdDecimal()
    {
        var hex = Activity.Current?.SpanId.ToString();
        if (hex is null) return null;
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var id)
            ? id.ToString() : hex;
    }

    public void RecordFaithfulness(double score, string sessionId, string domain)
    {
        score = Math.Clamp(score, 0.0, 1.0);
        _faithfulnessHistogram.Record(score,
            new KeyValuePair<string, object?>("session.id", sessionId),
            new KeyValuePair<string, object?>("query.domain", domain));
    }

    // ── Extract source citations from the most recent agent response ─────────
    // MCP tool results are nested JSON; each item often carries a "_source"
    // field. Walk the assistant + tool messages and collect distinct _source
    // values for the AgentResult.Sources list that the UI renders.
    private static List<string> ExtractSourcesFromResponse(AgentResponse response)
    {
        var sources = new List<string>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent fr && fr.Result is not null)
                {
                    TryExtractSources(fr.Result.ToString() ?? "", sources);
                }
                else if (content is TextContent tc)
                {
                    TryExtractSources(tc.Text, sources);
                }
            }
        }
        return sources;
    }

    // Raw tool RESULTS captured from the agent response. LLM-judge evaluators
    // (Groundedness) need this to verify answer claims against the actual data
    // the tools returned. Each result is the JSON / text string the model
    // received back from the tool — we cap each at 4KB to keep judge prompts
    // sane.
    private static List<string> ExtractToolResultsFromResponse(AgentResponse response)
    {
        const int maxChars = 4000;
        var results = new List<string>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent fr && fr.Result is not null)
                {
                    var s = fr.Result.ToString() ?? "";
                    if (s.Length > maxChars) s = s[..maxChars] + "…";
                    results.Add(s);
                }
            }
        }
        return results;
    }

    private static List<string> ExtractToolsCalledFromResponse(AgentResponse response)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent fc && seen.Add(fc.Name))
                    result.Add(fc.Name);
            }
        }
        return result;
    }

    private static void TryExtractSources(string maybeJson, List<string> sources)
    {
        if (string.IsNullOrWhiteSpace(maybeJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(maybeJson);
            WalkForSource(doc.RootElement, sources);
        }
        catch { /* not JSON — nothing to extract */ }
    }

    private static void WalkForSource(JsonElement el, List<string> sources)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("_source", out var src) && src.ValueKind == JsonValueKind.String)
            {
                var s = src.GetString();
                if (!string.IsNullOrEmpty(s) && !sources.Contains(s)) sources.Add(s);
            }
            foreach (var prop in el.EnumerateObject())
                WalkForSource(prop.Value, sources);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                WalkForSource(item, sources);
        }
    }

    // Lightweight keyword-based domain classifier — same logic as before,
    // kept for the AgentResult.QueryDomain field that downstream eval +
    // suggestion code reads.
    public static string ClassifyDomain(string query)
    {
        var q = query.ToLowerInvariant();
        foreach (var (domain, keywords) in DomainKeywords)
            if (keywords.Any(k => q.Contains(k))) return domain;
        return "general";
    }

    private static readonly Dictionary<string, List<string>> DomainKeywords = new()
    {
        ["engineering"]          = new() { "bridge", "highway", "rail", "nbi", "aadt", "sufficiency", "txdot", "traffic", "structural", "civil", "assessment", "inspection" },
        ["water"]                = new() { "water", "sdwis", "twdb", "pwsid", "violation", "desalination", "aquifer", "wastewater", "mep" },
        ["energy"]               = new() { "energy", "eia", "grid", "generation", "fuel", "solar", "wind", "ercot", "storage", "esr", "utility" },
        ["construction"]         = new() { "construction", "project delivery", "schedule", "commissioning", "site" },
        ["operations"]           = new() { "operations", "maintenance", "asset management", "facilities", "o&m", "lifecycle" },
        ["document"]             = new() { "draft", "scope of work", "sow", "risk summary", "cost estimate", "funding", "basis of design", "report", "memo" },
        ["business_development"] = new() { "rfp", "solicitation", "contract award", "procurement", "bid", "grant", "sam.gov", "usaspending", "competitive", "proposal", "opportunity" },
    };
}
