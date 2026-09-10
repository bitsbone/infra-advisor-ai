using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using InfraAdvisor.AgentApi.Models;
using InfraAdvisor.AgentApi.Services.Evaluators;
using StreamEvent = InfraAdvisor.AgentApi.Models.StreamEvent;

namespace InfraAdvisor.AgentApi.Services;

// Agent orchestrator backed by Microsoft Agents Framework.
//
// Router + 5 tool-partitioned specialists, wired via
// Microsoft.Agents.AI.Workflows' HandoffWorkflowBuilder (see
// SpecialistRegistry) — the .NET equivalent of Python's router +
// _TOOL_PARTITIONS pattern. MAF's .UseOpenTelemetry() (applied per agent in
// AgentHolder) emits the invoke_agent span; M.E.AI's .UseOpenTelemetry() on
// the shared chat client (set up in Program.cs) emits the chat +
// execute_tool spans inside it.
//
// Session memory + persistence: AgentSessionStore round-trips a plain
// List<ChatMessage> (user + final assistant text per turn — no tool-call
// replay), matching Python's agent-api exactly (services/agent-api/src/
// agent.py builds history_messages the same way from stored transcript
// text). See AgentSessionStore for why this replaced the old single-agent
// AgentSession serialize/deserialize once the workflow spanned 6 agents.
public class AgentService
{
    private readonly SpecialistRegistry _specialistRegistry;
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
    private readonly IContractAwardsEventPublisher _contractAwardsPublisher;
    private readonly ILogger<AgentService> _logger;

    // ActivitySource for manual spans that the M.E.AI / MAF decorators don't
    // emit on their own — task (classify_domain) here, retrieval inside
    // RetrievalService. Same source name TelemetrySetup AddSource's so they
    // get exported.
    private static readonly ActivitySource ActivitySource =
        new(Observability.TelemetrySetup.ActivitySourceName);

    public AgentService(
        SpecialistRegistry specialistRegistry,
        McpClientHolder mcpHolder,
        AgentSessionStore sessions,
        RetrievalService retrieval,
        IEnumerable<IResponseEvaluator> evaluators,
        DatadogEvalsClient ddEvals,
        DatadogAiGuardClient aiGuard,
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        IMeterFactory meterFactory,
        IContractAwardsEventPublisher contractAwardsPublisher,
        ILogger<AgentService> logger)
    {
        _specialistRegistry = specialistRegistry;
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
        _contractAwardsPublisher = contractAwardsPublisher;
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

    // Non-streaming variant — a thin aggregator over RunAgentStreamingAsync
    // rather than a second hand-rolled workflow-invocation path. Business
    // metrics, evals, and the retrieval/classify/AI-Guard pipeline all live
    // exactly once, in the streaming method; this just collects its
    // StreamEvents into an AgentResult for callers that don't want SSE.
    public async Task<AgentResult> RunAgentAsync(
        string query,
        string sessionId,
        string deployment,
        List<AttachmentDto>? attachments = null,
        string? rumSessionId = null,
        string? userId = null,
        string? jobRole = null,
        CancellationToken ct = default)
    {
        var answer = new System.Text.StringBuilder();
        var sources = new List<string>();
        var toolsCalled = new List<string>();
        var artifacts = new List<JsonElement>();
        var domain = "general";
        var isFirstEvent = true;

        await foreach (var evt in RunAgentStreamingAsync(query, sessionId, deployment, attachments, rumSessionId, userId, jobRole, ct))
        {
            switch (evt)
            {
                case ErrorEvent err when isFirstEvent:
                    // AI Guard blocks before anything else streams — the
                    // very first event on a blocked query is always this
                    // ErrorEvent, mirroring RunAgentStreamingAsync's guard
                    // check ordering.
                    return new AgentResult(
                        Answer: "",
                        Sources: sources,
                        ToolsCalled: toolsCalled,
                        QueryDomain: "blocked",
                        Blocked: true,
                        BlockReason: err.Message);
                case ErrorEvent err:
                    throw new InvalidOperationException(err.Message);
                case TextChunkEvent t:
                    answer.Append(t.Chunk);
                    break;
                case ArtifactEvent a:
                    artifacts.Add(a.Artifact);
                    break;
                case DoneEvent d:
                    domain = d.QueryDomain;
                    sources = d.Sources;
                    toolsCalled = d.ToolsCalled;
                    break;
            }
            isFirstEvent = false;
        }

        return new AgentResult(
            Answer: answer.ToString(),
            Sources: sources,
            ToolsCalled: toolsCalled,
            QueryDomain: domain,
            Artifacts: artifacts);
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
                    _logger.LogWarning("Evaluator {Label} failed error_type={ErrorType}", ev.Label, ex.GetType().Name);
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
        return ("The agent encountered an unexpected error. Please retry your question.", "unknown");
    }

    // Wraps ClassifyDomain in a manual Activity tagged so DD LLMObs renders
    // it as a "task" kind span (alongside the agent / chat / tool / embedding
    // / retrieval kinds emitted elsewhere in this trace).
    private static string ClassifyDomainTraced(string query)
    {
        using var activity = ActivitySource.StartActivity("classify_domain", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "classify_domain");
        activity?.SetTag("dd.llmobs.span.kind", "task");
        activity?.SetTag("query.characters", query.Length);

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
    // This manual span intentionally records only modality, MIME type, size,
    // duration, and status. The SAS URL, bytes, and transcript stay at the
    // provider boundary and are never copied into custom telemetry.
    private async Task<string?> TranscribeAudioIfPresentAsync(AttachmentDto audioAttachment, CancellationToken ct)
    {
        if (_whisperOpenAiClient is null)
        {
            _logger.LogWarning(
                "Skipping audio transcription because the Whisper client is not configured");
            return null;
        }

        using var activity = ActivitySource.StartActivity("transcribe_audio", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "transcribe_audio");
        activity?.SetTag("gen_ai.request.model", _whisperDeployment);
        activity?.SetTag("gen_ai.provider.name", "azure.ai.openai");
        activity?.SetTag("dd.llmobs.span.kind", "llm");

        foreach (var tag in SafeAttachmentTelemetry(audioAttachment))
            activity?.SetTag(tag.Key, tag.Value);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var audioBytes = await DownloadValidatedAudioAsync(audioAttachment, ct);
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

            activity?.SetTag("output.modality", "text");
            activity?.SetTag("transcript.characters", result.Value.Text.Length);
            activity?.SetTag("audio.duration_s", (stopwatch.ElapsedMilliseconds / 1000.0).ToString("F2"));
            return result.Value.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("transcribe_audio failed error_type={ErrorType}", ex.GetType().Name);
            activity?.SetTag("error", true);
            activity?.SetTag("error.type", ex.GetType().Name);
            return null;
        }
    }

    private async Task<byte[]> DownloadValidatedAudioAsync(AttachmentDto attachment, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, attachment.Url);
        using var response = await _mediaHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != attachment.SizeBytes)
            throw new InvalidAttachmentReferenceException();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream((int)attachment.SizeBytes);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            total += read;
            if (total > attachment.SizeBytes) throw new InvalidAttachmentReferenceException();
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != attachment.SizeBytes) throw new InvalidAttachmentReferenceException();
        return destination.ToArray();
    }

    internal static IReadOnlyDictionary<string, object> SafeAttachmentTelemetry(AttachmentDto attachment) =>
        new Dictionary<string, object>
        {
            ["input.modality"] = attachment.Kind,
            [$"{attachment.Kind}.mime_type"] = attachment.MimeType,
            [$"{attachment.Kind}.size_bytes"] = attachment.SizeBytes,
        };

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
        string? rumSessionId = null,
        string? userId = null,
        string? jobRole = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // See RunAgentAsync for why this prefers the real RUM session over
        // the tenant-scoped sessionId.
        AmbientSessionContext.Set(rumSessionId ?? sessionId);

        _logger.LogDebug(
            "[stream] starting; ct already cancelled: {AlreadyCancelled}",
            ct.IsCancellationRequested);

        // 0. AI Guard pre-flight check — see RunAgentAsync for rationale.
        //    Streaming can't rewind text already sent to the client, so this
        //    must run before the first StreamEvent goes out.
        var guardResult = await _aiGuard.EvaluateAsync(
            new[] { new AiGuardMessage("user", query) }, ct);
        if (guardResult.IsBlocked)
        {
            _logger.LogWarning(
                "AI Guard blocked streaming query action={Action}",
                guardResult.Action);
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

        // Per-user targeted resolution — targetingKey is the user id,
        // attributes carries the demo job_role value when the JWT has one.
        // Falls back to the cached pod-wide default workflow when there's
        // no matching per-user override — see SpecialistRegistry.
        var targetingAttributes = jobRole is not null
            ? new Dictionary<string, string> { ["job_role"] = jobRole }
            : null;
        var workflow = await _specialistRegistry.GetWorkflowForRequestAsync(userId, targetingAttributes, ct);
        var history = await _sessions.GetOrCreateHistoryAsync(sessionId, ct);
        history.Add(inputMessage);

        // Track tool-call lifecycle as the model emits FunctionCallContent
        // / FunctionResultContent updates. Start times are captured at
        // ToolCallStart so the End event reports duration even when MAF
        // emits the call + result back-to-back in a single update batch.
        var toolStarts = new Dictionary<string, (long StartTicks, string Name)>();
        var argsJsonByCallId = new Dictionary<string, string?>();
        var allSources = new List<string>();
        var toolsCalledOrdered = new List<string>();
        var toolResults = new List<string>();
        var fullAnswer = new System.Text.StringBuilder();
        Exception? streamError = null;

        // ExecutorId (see AgentHolder — "router", "specialist-engineering",
        // ...) of whichever agent most recently produced visible text —
        // i.e. the specialist that actually answered this turn, for the
        // per-turn prompt-version badge on DoneEvent below. The router's
        // own updates carry only handoff FunctionCallContent, never
        // TextContent, so this naturally settles on the answering specialist.
        string? lastExecutorId = null;

        // Whether anything the user can already see (a tool chip or answer
        // text) has been yielded yet. A session-expired reconnect is only
        // safe to retry transparently while this is still false — once
        // something has streamed, replaying the query would duplicate or
        // contradict what the client already rendered.
        var anyStreamed = false;
        var retriedMcpSession = false;

        var run = await InProcessExecution.RunStreamingAsync(workflow, history, sessionId: sessionId, cancellationToken: ct);
        // HandoffStartExecutor (like every ChatProtocolExecutor) only takes a
        // turn when it receives an explicit TurnToken — sending just the
        // message history buffers it via AddMessagesAsync and stops there.
        // InProcessExecution.RunAsync (non-streaming) auto-sends one for
        // chat-protocol workflows; RunStreamingAsync does not (confirmed by
        // decompiling InProcessExecutionEnvironment 1.20.0 — RunAsync routes
        // through BeginRunHandlingChatProtocolAsync, which appends
        // `new TurnToken(true)` when the input isn't already one;
        // RunStreamingAsync's EnqueueAndStreamAsync path has no equivalent).
        // Without this, the turn silently never runs: no router/specialist
        // invocation, no tool calls, no answer, no error — just a 200 with
        // nothing streamed. `true` also turns on AgentResponseUpdateEvent
        // emission for this turn, since SpecialistRegistry's
        // HandoffWorkflowBuilder never calls EmitAgentResponseUpdateEvents()
        // itself.
        await run.TrySendMessageAsync(new TurnToken(true));
        var events = run.WatchStreamAsync(ct);
        var enumerator = events.GetAsyncEnumerator(ct);

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
                    "MCP session expired before any streamed output; reconnecting once error_type={ErrorType}",
                    ex.GetType().Name);
                _mcpReconnectCounter.Add(1,
                    new KeyValuePair<string, object?>("reason", "session_expired_stream"));
                await enumerator.DisposeAsync();
                await run.DisposeAsync();
                await _mcpHolder.RefreshAsync(ct);
                // Rebuilds every specialist's agent since McpClientHolder.
                // Generation changed — same generation-check shape
                // AgentHolder already used for the single-agent path.
                workflow = await _specialistRegistry.GetWorkflowForRequestAsync(userId, targetingAttributes, ct);
                toolStarts.Clear();
                allSources.Clear();
                toolsCalledOrdered.Clear();
                toolResults.Clear();
                fullAnswer.Clear();
                run = await InProcessExecution.RunStreamingAsync(workflow, history, sessionId: sessionId, cancellationToken: ct);
                await run.TrySendMessageAsync(new TurnToken(true));
                events = run.WatchStreamAsync(ct);
                enumerator = events.GetAsyncEnumerator(ct);
                continue;
            }
            catch (Exception ex)
            {
                streamError = ex;
                break;
            }
            if (!moved) break;

            // Only agent output events carry model content — lifecycle
            // events (WorkflowStartedEvent, SuperStepEvent, ...) are
            // ignored here.
            if (enumerator.Current is not AgentResponseUpdateEvent updateEvent) continue;
            var update = updateEvent.Update;
            foreach (var ev in HandleUpdate(update, toolStarts, allSources, toolsCalledOrdered, toolResults, fullAnswer))
            {
                if (ev is ToolCallStartEvent startEv) argsJsonByCallId[startEv.Id] = startEv.ArgsJson;
                if (ev is ArtifactEvent artifactEv)
                {
                    var toolCallId = artifactEv.Artifact.TryGetProperty("tool_call_id", out var tcid) && tcid.ValueKind == JsonValueKind.String
                        ? tcid.GetString()
                        : null;
                    object? queryInput = toolCallId is not null && argsJsonByCallId.TryGetValue(toolCallId, out var argsJson) && argsJson is not null
                        ? JsonSerializer.Deserialize<JsonElement>(argsJson)
                        : null;
                    PublishContractAwardsIfApplicable(artifactEv.Artifact, sessionId, queryInput);
                }
                if (ev is ToolCallStartEvent or TextChunkEvent) anyStreamed = true;
                if (ev is TextChunkEvent) lastExecutorId = updateEvent.ExecutorId;
                yield return ev;
            }
        }
        await enumerator.DisposeAsync();
        await run.DisposeAsync();

        if (streamError is not null)
        {
            var (errorMessage, errorCategory) = ClassifyStreamError(streamError, retriedMcpSession);
            _logger.LogWarning(
                "agent workflow run failed category={Category} error_type={ErrorType}",
                errorCategory, streamError.GetType().Name);
            // Persist whatever history accumulated before the failure (at
            // minimum the user's own message) so a transient error or a
            // client disconnect mid-stream doesn't silently wipe context for
            // the next turn. Use CancellationToken.None: `ct` may already be
            // cancelled (e.g. client disconnect), and we still want this
            // write to go through.
            await _sessions.SaveHistoryAsync(sessionId, history, CancellationToken.None);
            yield return new ErrorEvent(
                errorMessage, TraceId: Activity.Current?.TraceId.ToString(), Category: errorCategory);
            yield break;
        }

        // Text-only history round trip — matches Python's agent-api exactly
        // (services/agent-api/src/agent.py rebuilds history_messages from
        // stored HumanMessage/AIMessage text, never replaying tool calls).
        history.Add(new ChatMessage(ChatRole.Assistant, fullAnswer.ToString()));
        await _sessions.SaveHistoryAsync(sessionId, history, ct);

        var domainTag = new KeyValuePair<string, object?>("query.domain", domain);
        _conversationCounter.Add(1, domainTag);
        foreach (var tool in toolsCalledOrdered.Distinct())
            _toolCounter.Add(1,
                new KeyValuePair<string, object?>("tool.name", tool),
                domainTag);

        var distinctSources = allSources.Distinct().ToList();
        var distinctTools = toolsCalledOrdered.Distinct().ToList();

        ScheduleEvaluations(effectiveQuery, fullAnswer.ToString(), distinctTools, toolResults, distinctSources, domain);

        // Reads the pod-wide cached PromptHolder.Current for whichever
        // specialist answered. Known gap: when GetWorkflowForRequestAsync
        // built a per-user override agent (a targeting rule actually fired
        // differently for this user), this still reports the pod-wide
        // default's version/source rather than the override that literally
        // answered — surfacing the override's resolved value here would
        // require threading it out of AgentHolder.GetAgentForRequestAsync
        // alongside the built agent. Acceptable for now: overrides are the
        // rare case (see GetWorkflowForRequestAsync's own comment), and the
        // badge is still correct whenever no per-user override fires.
        PromptFetchResult? answeringPrompt = lastExecutorId is not null
            ? _specialistRegistry.PromptHolders.GetValueOrDefault(lastExecutorId)?.Current
            : null;

        yield return new DoneEvent(
            TraceId: GetTraceIdDecimal(),
            SpanId: GetSpanIdDecimal(),
            SessionId: sessionId,
            Model: deployment,
            Sources: distinctSources,
            ToolsCalled: distinctTools,
            QueryDomain: domain,
            PromptId: lastExecutorId,
            PromptVersion: answeringPrompt?.Version,
            PromptSource: answeringPrompt?.Source);
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
                    var artifact = ChatArtifactParser.TryExtract(resultStr, name, fr.CallId);
                    if (artifact is not null) yield return new ArtifactEvent(artifact.Value);
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
            new KeyValuePair<string, object?>("query.domain", domain));
    }

    private void PublishContractAwardsIfApplicable(JsonElement artifact, string sessionId, object? queryInput)
    {
        if (!artifact.TryGetProperty("kind", out var kind) || kind.GetString() != "contract_awards") return;
        var toolCallId = artifact.TryGetProperty("tool_call_id", out var tcid) && tcid.ValueKind == JsonValueKind.String
            ? tcid.GetString()
            : null;
        _contractAwardsPublisher.Publish(sessionId, toolCallId, queryInput, artifact.GetProperty("items"));
    }

    private static void TryExtractSources(string maybeJson, List<string> sources)
    {
        if (string.IsNullOrWhiteSpace(maybeJson)) return;
        try
        {
            var artifact = ChatArtifactParser.TryExtract(maybeJson, null, null);
            if (artifact is not null)
                foreach (var source in ChatArtifactParser.ExtractSourceUrls(artifact.Value))
                    if (!sources.Contains(source)) sources.Add(source);
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
