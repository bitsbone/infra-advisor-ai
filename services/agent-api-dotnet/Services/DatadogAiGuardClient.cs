using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InfraAdvisor.AgentApi.Services;

// Thin HTTP client wrapping DD's AI Guard evaluate API.
//
//   POST https://api.<site>/api/v2/ai-guard/evaluate
//
// Unlike DatadogEvalsClient (LLM Obs eval-metric intake), AI Guard's HTTP
// API path sends NO traces to Datadog — that's a hard platform limitation
// documented by DD (the SDK path, which requires a framework we don't use
// here, is the only one with native trace visibility). We compensate with:
//   - a manual Activity span (tagged ai_guard.*) so the decision is still
//     visible as a normal APM span nested under the request trace
//   - a structured log line, picked up by the existing
//     DatadogTraceContextEnricher for dd.trace_id/dd.span_id correlation
//   - the AiGuardSubmissionLog ring buffer, surfaced via GET /ai-guard/status
//     and the admin diagnostics panel
//
// Disabled gracefully when DD_API_KEY or DD_APPLICATION_KEY isn't set, and
// fails OPEN (treats as ALLOW) on any transport/parse error — a Datadog
// outage or misconfiguration must never block legitimate traffic.
public class DatadogAiGuardClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DatadogAiGuardClient> _logger;
    private readonly AiGuardSubmissionLog _log;
    private readonly string? _apiKey;
    private readonly string? _appKey;
    private readonly string _site;
    private readonly bool _enabled;

    private static readonly ActivitySource ActivitySource =
        new(Observability.TelemetrySetup.ActivitySourceName);

    public DatadogAiGuardClient(
        HttpClient http,
        ILogger<DatadogAiGuardClient> logger,
        AiGuardSubmissionLog log)
    {
        _http = http;
        _logger = logger;
        _log = log;
        _apiKey = Environment.GetEnvironmentVariable("DD_API_KEY");
        _appKey = Environment.GetEnvironmentVariable("DD_APPLICATION_KEY");
        _site = Environment.GetEnvironmentVariable("DD_SITE") ?? "datadoghq.com";
        _enabled = !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_appKey);

        if (!_enabled)
            _logger.LogWarning(
                "DD_API_KEY/DD_APPLICATION_KEY not both set; DatadogAiGuardClient disabled — requests will not be evaluated (fail open).");
    }

    public bool Enabled => _enabled;

    // Evaluates a message transcript; AI Guard evaluates the final message,
    // which for our V1 pre-flight check is always the incoming user query.
    public async Task<AiGuardEvaluation> EvaluateAsync(
        IReadOnlyList<AiGuardMessage> messages,
        CancellationToken ct = default)
    {
        if (!_enabled)
        {
            _log.Record(new AiGuardSubmissionEntry(
                Timestamp: DateTimeOffset.UtcNow,
                Action: "ALLOW",
                Reason: "AI Guard disabled — DD_API_KEY/DD_APPLICATION_KEY not set",
                TraceIdDecimal: GetTraceIdDecimal(),
                SpanIdDecimal: GetSpanIdDecimal(),
                Success: false,
                DurationMs: 0,
                Error: "AI Guard disabled"));
            return new AiGuardEvaluation("ALLOW", "AI Guard disabled");
        }

        // Span name (not just resource) must be the bare "ai_guard" — Datadog's
        // Security > AI Guard investigate page filters on resource_name:ai_guard
        // exactly (confirmed via its query string), and OTel's span name becomes
        // the APM resource_name on ingestion. ddtrace's own span uses this same
        // bare name, confirmed against a live Python-backend trace.
        using var activity = ActivitySource.StartActivity("ai_guard", ActivityKind.Client);
        var traceIdDecimal = GetTraceIdDecimal();
        var spanIdDecimal = GetSpanIdDecimal();

        var startedAt = DateTimeOffset.UtcNow;
        var success = false;
        var action = "ALLOW";
        string? reason = null;
        string? error = null;
        string[]? attackCategories = null;

        // Our pre-flight check only ever evaluates the raw user query today —
        // ddtrace's SDK also reports "tool" for tool-call content, which we
        // don't check yet. Wired as a constant now rather than over-building
        // for a case we don't have (confirmed via a real Python-backend
        // trace — see docs/llm-engineering/ai-guard.mdx).
        activity?.SetTag("ai_guard.target", "prompt");

        try
        {
            var payload = new
            {
                data = new
                {
                    attributes = new
                    {
                        messages = messages.Select(m => (object)new { role = m.Role, content = m.Content }),
                    },
                },
            };

            var url = $"https://api.{_site}/api/v2/ai-guard/evaluate";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("DD-API-KEY", _apiKey);
            req.Headers.TryAddWithoutValidation("DD-APPLICATION-KEY", _appKey);
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json"));

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                error = $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}";
                _logger.LogWarning("AI Guard evaluate call failed: {Status} — {Body}",
                    (int)resp.StatusCode, Truncate(body, 200));
            }
            else
            {
                success = true;
                using var doc = JsonDocument.Parse(body);
                var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
                action = attrs.TryGetProperty("action", out var a) ? a.GetString() ?? "ALLOW" : "ALLOW";
                reason = attrs.TryGetProperty("reason", out var r) ? r.GetString() : null;
                if (attrs.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                    attackCategories = tagsEl.EnumerateArray()
                        .Select(t => t.GetString())
                        .Where(t => t is not null)
                        .Select(t => t!)
                        .ToArray();
            }
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {Truncate(ex.Message, 120)}";
            _logger.LogWarning("AI Guard evaluate call threw: {Error}", ex.Message);
        }
        finally
        {
            var durationMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            var blocked = success && action is "DENY" or "ABORT";
            activity?.SetTag("ai_guard.action", action);
            activity?.SetTag("ai_guard.reason", reason);
            activity?.SetTag("ai_guard.duration_ms", durationMs);
            activity?.SetTag("ai_guard.success", success);
            // Presence signals a block — matches ddtrace's own convention
            // (no "false" tag when allowed), confirmed against a real trace.
            if (blocked) activity?.SetTag("ai_guard.blocked", "true");
            if (attackCategories is { Length: > 0 })
                activity?.SetTag("ai_guard.attack_categories", attackCategories);

            // ddtrace's own context-manager marks the span as errored for a
            // real AIGuardAbortError exception — confirmed on a live trace
            // (blocked, not just a transport failure, ends up status:error
            // with a captured exception). Our manual Activity needs this
            // set explicitly for the same two cases.
            if (error is not null)
                activity?.SetStatus(ActivityStatusCode.Error, error);
            else if (blocked)
                activity?.SetStatus(ActivityStatusCode.Error, reason);

            // ddtrace also stamps these on the trace's root span so a
            // trace/monitor query can find "requests with an AI Guard
            // evaluation" without opening this child span.
            TagRootSpan(activity, "ai_guard.event", "true");
            TagRootSpan(activity, "ai_guard.calling_service", Observability.TelemetrySetup.ActivitySourceName);

            _log.Record(new AiGuardSubmissionEntry(
                Timestamp: startedAt,
                Action: success ? action : "ALLOW",
                Reason: TruncateForLog(reason ?? error),
                TraceIdDecimal: traceIdDecimal,
                SpanIdDecimal: spanIdDecimal,
                Success: success,
                DurationMs: durationMs,
                Error: error));

            _logger.LogInformation(
                "ai_guard.evaluate action={Action} success={Success} duration_ms={DurationMs} dd.trace_id={TraceId} dd.span_id={SpanId}",
                action, success, durationMs, traceIdDecimal, spanIdDecimal);
        }

        // Fail open: a transport/parse error should never block a legitimate
        // request. Only an explicit DENY/ABORT from a successful call blocks.
        return new AiGuardEvaluation(success ? action : "ALLOW", reason ?? error);
    }

    // Walks up to the trace's local-root span (ASP.NET Core's own
    // auto-instrumented request Activity, from OpenTelemetry.Instrumentation
    // .AspNetCore — still open for the whole request) and tags it, mirroring
    // ddtrace's span_bus.get_root_span() root-only tagging. No AsyncLocal
    // capture needed here (unlike AgentSpanContext/AmbientSessionContext):
    // the root Activity is still current/reachable via .Parent at this
    // point in the request, not read back later after it's gone.
    private static void TagRootSpan(Activity? start, string key, string? value)
    {
        var root = start ?? Activity.Current;
        while (root?.Parent is not null) root = root.Parent;
        root?.SetTag(key, value);
    }

    private static string? GetTraceIdDecimal()
    {
        var hex = Activity.Current?.TraceId.ToString();
        if (hex is not { Length: 32 }) return hex;
        return ulong.TryParse(hex[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lo)
            ? lo.ToString(CultureInfo.InvariantCulture) : hex;
    }

    private static string? GetSpanIdDecimal()
    {
        var hex = Activity.Current?.SpanId.ToString();
        if (hex is null) return null;
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
            ? id.ToString(CultureInfo.InvariantCulture) : hex;
    }

    private static string? TruncateForLog(string? s) => s is null ? null : Truncate(s, 240);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

public record AiGuardMessage(string Role, string Content);

public record AiGuardEvaluation(string Action, string? Reason)
{
    public bool IsBlocked => Action is "DENY" or "ABORT";
}
