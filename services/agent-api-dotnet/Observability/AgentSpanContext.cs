using System.Diagnostics;
using System.Globalization;
using OpenTelemetry;

namespace InfraAdvisor.AgentApi.Observability;

// Hands the answering agent's span IDs to AgentService so it can attach
// external evaluations to the AGENT span (not the HTTP root) — DD requires
// both IDs on the eval-metric API's join_on.span field.
//
// Two constraints shape this:
//   - The IDs are only known inside MAF, which starts and ends the agent span
//     deep in its own async code. An AsyncLocal value SET there never flows
//     back to AgentService, so BeginRequest stores an empty Slot up front and
//     the processor fills that same object in place, which the caller sees.
//   - MAF names a span invoke_agent only after it starts (it renames the chat
//     activity OpenTelemetryChatClient started — see PromptTrackingProcessor),
//     so capture happens at span end, in Processor.
// The router ends before the specialist it hands off to, so the last
// invoke_agent to end — the specialist that answered — is the one kept.
//
// DD's eval-metric API accepts decimal-string OR 32-char hex for trace_id
// and decimal-string for span_id. We capture both raw formats and let the
// caller pick the conversion it needs.
public static class AgentSpanContext
{
    private static readonly AsyncLocal<Slot?> _slot = new();

    public static Captured? Current => _slot.Value?.Captured;

    // Call once per request, before the agent runs and before any
    // ExecutionContext.Capture that the run restores.
    public static void BeginRequest() => _slot.Value = new Slot();

    public static void Capture(Activity activity)
    {
        if (_slot.Value is not { } slot) return;
        var traceHex = activity.TraceId.ToString();
        var spanHex = activity.SpanId.ToString();
        slot.Captured = new Captured(
            TraceIdHex: traceHex,
            SpanIdHex: spanHex,
            TraceIdDecimal: HexLowerToDecimal(traceHex),
            SpanIdDecimal: HexToDecimal(spanHex));
    }

    public sealed class Processor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            if (activity.GetTagItem("gen_ai.operation.name") as string == "invoke_agent")
                Capture(activity);
        }
    }

    private sealed class Slot
    {
        public volatile Captured? Captured;
    }

    // 64-bit decimal from a hex string. For 32-char trace IDs we take the
    // low 16 hex chars (low 64 bits) to match DD APM's trace_id form.
    private static string HexLowerToDecimal(string hex)
    {
        if (hex.Length == 32) hex = hex[16..];
        return HexToDecimal(hex);
    }

    private static string HexToDecimal(string hex)
    {
        if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return v.ToString(CultureInfo.InvariantCulture);
        return hex;
    }

    public record Captured(
        string TraceIdHex,
        string SpanIdHex,
        string TraceIdDecimal,
        string SpanIdDecimal);
}
