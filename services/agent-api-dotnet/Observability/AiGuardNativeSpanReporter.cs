using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using MessagePack;

namespace InfraAdvisor.AgentApi.Observability;

// EXPERIMENTAL, additive-only span reporter.
//
// Datadog's Security > AI Guard investigate page has a list view (works
// today via the OTel-based `ai_guard` Activity in DatadogAiGuardClient) and
// a detail side panel, which strictly requires `event.custom.ai_guard` — a
// field only ever populated from a span's `meta_struct`, a Datadog-native
// trace-agent-protocol concept with no OTLP equivalent (confirmed this
// session by reading ddtrace-python's aiguard package, dd-trace-dotnet's
// Activity->Span bridge — which has no code path that would ever populate
// it — and by capturing a real v0.4/traces payload via HTTP Toolkit against
// a live ddtrace-python AI Guard block).
//
// Rather than adopt the full dd-trace-dotnet tracer (which would gain us
// nothing else here — .NET has no LLM auto-instrumentation to begin with,
// per Datadog's own LLM Observability docs — and would conflict with the
// admission-controller-injected AAP-only tracer already in this pod), this
// is a narrow, standalone client that speaks the Agent's native v0.4/traces
// msgpack protocol directly for exactly one span, mirroring how
// Datadog.FeatureFlags.OpenFeature pulls in one specific Datadog capability
// without the full tracer. It mints its own span_id (a genuine second span,
// sibling to the existing OTel `ai_guard` Activity, not a replacement) and
// reuses the current trace's trace_id/parent span_id so it's stitched into
// the same distributed trace.
//
// Off by default (DD_AI_GUARD_NATIVE_SPAN_ENABLED) and fully best-effort:
// any failure here is logged and swallowed, never surfaced to the caller —
// this must never affect the real evaluate() result or request latency in
// any way that matters. Verify against a live Security > AI Guard page
// before relying on this for anything.
public sealed class AiGuardNativeSpanReporter
{
    private readonly HttpClient _http;
    private readonly ILogger<AiGuardNativeSpanReporter> _logger;
    private readonly bool _enabled;
    private readonly string _agentUrl;
    private readonly string _service;

    public AiGuardNativeSpanReporter(HttpClient http, ILogger<AiGuardNativeSpanReporter> logger)
    {
        _http = http;
        _logger = logger;
        _enabled = Environment.GetEnvironmentVariable("DD_AI_GUARD_NATIVE_SPAN_ENABLED")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        // Deliberately NOT "DD_TRACE_AGENT_URL" — the admission-controller-injected
        // dd-trace-dotnet tracer already reads that var in this pod, pointed at a
        // Unix domain socket (unix:///var/run/datadog/apm.socket) for its own AAP
        // telemetry. Reusing that name here made this reporter pick up the unix://
        // URL and fail with "the 'unix' scheme is not supported" (confirmed live).
        _agentUrl = Environment.GetEnvironmentVariable("DD_AI_GUARD_NATIVE_AGENT_URL")
            ?? "http://datadog-agent.datadog.svc.cluster.local:8126";
        _service = Environment.GetEnvironmentVariable("DD_SERVICE")
            ?? Observability.TelemetrySetup.ActivitySourceName;
    }

    public record Message(string Role, string Content);

    public sealed record SpanData(
        DateTimeOffset StartedAt,
        TimeSpan Duration,
        bool Error,
        IReadOnlyDictionary<string, string> Meta,
        IReadOnlyList<Message> Messages,
        IReadOnlyList<string> AttackCategories,
        IReadOnlyDictionary<string, double> TagProbs);

    public async Task ReportAsync(SpanData data, CancellationToken ct = default)
    {
        if (!_enabled) return;

        try
        {
            var (traceId, parentId, traceIdHigh) = GetCurrentIds();
            if (traceId == 0)
            {
                _logger.LogDebug("AiGuardNativeSpanReporter: no active trace context, skipping");
                return;
            }

            var spanId = NewSpanId();
            var bytes = Encode(traceId, spanId, parentId, traceIdHigh, data);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_agentUrl.TrimEnd('/')}/v0.4/traces");
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/msgpack");
            req.Headers.TryAddWithoutValidation("X-Datadog-Trace-Count", "1");
            req.Headers.TryAddWithoutValidation("Datadog-Meta-Lang", "dotnet");
            req.Headers.TryAddWithoutValidation("Datadog-Meta-Lang-Interpreter", "dotnet-experimental-aiguard-reporter");
            req.Headers.TryAddWithoutValidation("Datadog-Client-Computed-Top-Level", "true");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "AiGuardNativeSpanReporter: agent rejected v0.4/traces payload, status={Status} body={Body}",
                    (int)resp.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // Best-effort only — never let this affect the real evaluate() path.
            _logger.LogWarning(ex, "AiGuardNativeSpanReporter: failed to submit native span (non-fatal)");
        }
    }

    // traceIdHigh is the upper 64 bits of the W3C 128-bit trace ID, as the
    // 16-char hex string Datadog's own `_dd.p.tid` propagation tag uses
    // (confirmed present, e.g. "6aa38bad00000000", on the Python reference
    // trace). Our wire-format `trace_id` field only carries the low 64 bits
    // (all v0.4 supports); without `_dd.p.tid` alongside it, the backend
    // can't reconstruct the full 128-bit ID other spans in this same trace
    // already carry, breaking cross-linking to the LLM/Agent Observability
    // trace — confirmed live ("Could not find corresponding trace in Agent
    // Observability" on this span's detail panel).
    private static (ulong traceId, ulong parentId, string? traceIdHigh) GetCurrentIds()
    {
        var current = Activity.Current;
        if (current is null) return (0, 0, null);

        var traceHex = current.TraceId.ToString();
        var traceId = traceHex.Length == 32
            && ulong.TryParse(traceHex[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var t)
            ? t : 0;
        var traceIdHigh = traceHex.Length == 32 ? traceHex[..16] : null;

        var spanHex = current.SpanId.ToString();
        var parentId = ulong.TryParse(spanHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p)
            ? p : 0;

        return (traceId, parentId, traceIdHigh);
    }

    private static ulong NewSpanId()
    {
        // Datadog span IDs are 63-bit non-zero unsigned values (top bit
        // reserved). Random.Shared is fine here — collision risk at our
        // volume is negligible and this is an experimental secondary span.
        Span<byte> buf = stackalloc byte[8];
        ulong id;
        do
        {
            Random.Shared.NextBytes(buf);
            id = BitConverter.ToUInt64(buf) & 0x7FFF_FFFF_FFFF_FFFF;
        } while (id == 0);
        return id;
    }

    // Hand-written msgpack encoding matching the exact v0.4/traces span shape
    // captured live from a real ddtrace-python AI Guard evaluation (via HTTP
    // Toolkit): a top-level array-of-traces / array-of-spans / span-map, with
    // `meta_struct` as a plain nested map — {"ai_guard": {"messages": [...],
    // "attack_categories": [...], "tag_probs": {...}}} — sibling to `meta`/
    // `metrics`, not a special pre-encoded byte-blob format.
    private byte[] Encode(ulong traceId, ulong spanId, ulong parentId, string? traceIdHigh, SpanData data)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);

        writer.WriteArrayHeader(1); // 1 trace
        writer.WriteArrayHeader(1); // 1 span in this trace

        var spanFieldCount = 11; // service,name,resource,trace_id,span_id,parent_id,start,duration,error,meta,meta_struct
        writer.WriteMapHeader(spanFieldCount);

        writer.Write("service"); writer.Write(_service);
        writer.Write("name"); writer.Write("ai_guard");
        writer.Write("resource"); writer.Write("ai_guard");
        writer.Write("trace_id"); writer.Write(traceId);
        writer.Write("span_id"); writer.Write(spanId);
        writer.Write("parent_id"); writer.Write(parentId);
        writer.Write("start"); writer.Write(data.StartedAt.ToUnixTimeMilliseconds() * 1_000_000L);
        writer.Write("duration"); writer.Write((long)(data.Duration.TotalMilliseconds * 1_000_000));
        writer.Write("error"); writer.Write(data.Error ? 1 : 0);

        writer.Write("meta");
        writer.WriteMapHeader(data.Meta.Count + (traceIdHigh is not null ? 1 : 0));
        foreach (var (k, v) in data.Meta)
        {
            writer.Write(k);
            writer.Write(v);
        }
        if (traceIdHigh is not null)
        {
            writer.Write("_dd.p.tid");
            writer.Write(traceIdHigh);
        }

        // meta_struct's per-key VALUES must be msgpack `bin` (a length-prefixed
        // binary blob whose contents are themselves separately msgpack-encoded)
        // — NOT a directly-nested map. Confirmed against the real Agent's own
        // decoder: an earlier attempt that wrote a plain nested map here was
        // rejected with "msgp: attempted to decode type \"map\" with method for
        // \"bin\" at 0/0/MetaStruct/ai_guard". dd-trace-dotnet's own internal
        // Span.SetMetaStruct(string key, byte[] value) signature — pre-encoded
        // bytes, not an object — was the correct hint all along.
        writer.Write("meta_struct");
        writer.WriteMapHeader(1);
        writer.Write("ai_guard");
        writer.Write(EncodeAiGuardStruct(data));

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] EncodeAiGuardStruct(SpanData data)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);

        var structFieldCount = 1
            + (data.Messages.Count > 0 ? 1 : 0)
            + (data.AttackCategories.Count > 0 ? 1 : 0)
            + (data.TagProbs.Count > 0 ? 1 : 0);
        writer.WriteMapHeader(structFieldCount);

        writer.Write("messages");
        writer.WriteArrayHeader(data.Messages.Count);
        foreach (var m in data.Messages)
        {
            writer.WriteMapHeader(2);
            writer.Write("role"); writer.Write(m.Role);
            writer.Write("content"); writer.Write(m.Content);
        }

        // The web UI's list/investigate view surfaces `@ai_guard.current` as
        // a column — the V1 envelope's single "message currently being
        // evaluated" field, distinct from V2's `messages` array above. The
        // detail side panel accepts either shape (isEither(isEvaluationEnvelopeV1,
        // isEvaluationEnvelopeV2)) and already renders correctly with just
        // `messages`, but the list view's Content column showed "No content"
        // for this span — confirmed live — which V1's `current` likely
        // drives instead. Sending both costs nothing and satisfies whichever
        // view reads which field, rather than guessing which one to pick.
        if (data.Messages.Count > 0)
        {
            var current = data.Messages[^1];
            writer.Write("current");
            writer.WriteMapHeader(2);
            writer.Write("role"); writer.Write(current.Role);
            writer.Write("content"); writer.Write(current.Content);
        }

        if (data.AttackCategories.Count > 0)
        {
            writer.Write("attack_categories");
            writer.WriteArrayHeader(data.AttackCategories.Count);
            foreach (var c in data.AttackCategories) writer.Write(c);
        }

        if (data.TagProbs.Count > 0)
        {
            writer.Write("tag_probs");
            writer.WriteMapHeader(data.TagProbs.Count);
            foreach (var (k, v) in data.TagProbs)
            {
                writer.Write(k);
                writer.Write(v);
            }
        }

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
