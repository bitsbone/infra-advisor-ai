using System.Diagnostics.Tracing;

namespace AcaAgenticPoc.Observability;

// Bridges the OpenTelemetry .NET SDK's internal EventSources (span
// processor activity, OTLP exporter connection/export failures, sampler
// decisions) straight to stdout, so they show up in `az containerapp logs
// show` without needing container exec access.
//
// This exists because the SDK's own file-based "self-diagnostics" feature
// (https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry/README.md#self-diagnostics)
// writes to a log file on disk — not useful here, since this app's minimal
// container images have no shell tools to read files with (confirmed via
// az containerapp exec: no cat, no ps, busybox `sh` only) and file-based
// logs would need painful/rate-limited exec sessions to retrieve at all.
// Console output is already accessible via the platform's log stream.
//
// Opt-in via OTEL_TRACE_DEBUG=true (not on by default — this is genuinely
// verbose, one line per span start/end/export attempt).
public sealed class OtelDiagnosticsListener : EventListener
{
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
        {
            EnableEvents(eventSource, EventLevel.Verbose);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        string message;
        try
        {
            message = eventData.Payload is { Count: > 0 } && eventData.Message is not null
                ? string.Format(eventData.Message, [.. eventData.Payload])
                : eventData.Message ?? eventData.EventName ?? "(no message)";
        }
        catch (FormatException)
        {
            message = eventData.Message ?? eventData.EventName ?? "(unformattable event)";
        }

        Console.WriteLine($"[otel-diag] {eventData.EventSource.Name}/{eventData.EventName}: {message}");
    }
}
