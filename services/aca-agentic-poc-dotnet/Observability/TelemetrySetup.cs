using Microsoft.AspNetCore.Builder;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AcaAgenticPoc.Observability;

// OpenTelemetry setup for the ACA agentic POC — deliberately different from
// services/agent-api-dotnet/Observability/TelemetrySetup.cs in ONE important
// way: this app is deployed twice, side by side, comparing two different
// OTel-to-Datadog paths (see infra/bicep/modules/aca-agentic-poc.bicep):
//
//   - aca-agentic-poc-managed: ACA's platform-managed OTel agent auto-injects
//     OTEL_EXPORTER_OTLP_ENDPOINT (gRPC-only, no path suffix).
//   - aca-agentic-poc-sidecar: Bicep explicitly sets
//     OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 (HTTP/protobuf,
//     the datadog-sidecar container's OTLP receiver).
//
// Since the exact same container image runs both, the exporter must pick its
// protocol/endpoint from environment variables at the OTel SDK level rather
// than a hardcoded choice — calling AddOtlpExporter() with NO configuration
// delegate lets the SDK read OTEL_EXPORTER_OTLP_ENDPOINT/_PROTOCOL/_HEADERS
// directly per the OpenTelemetry spec, instead of this app assuming one
// protocol or manually appending a signal-specific path suffix the way
// agent-api-dotnet's TelemetrySetup.cs does (that service only ever talks to
// one destination, so it can hardcode HttpProtobuf + explicit /v1/traces —
// this app can't make that assumption).
public static class TelemetrySetup
{
    // Passed to Microsoft.Agents.AI's .UseOpenTelemetry(sourceName:) call on
    // the agent builder — same value gets AddSource'd below so the
    // invoke_agent span is exported.
    public const string ActivitySourceName = "aca-agentic-poc-dotnet";

    public static void Configure(WebApplicationBuilder builder)
    {
        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            ?? "aca-agentic-poc-dotnet";
        var ddEnv = Environment.GetEnvironmentVariable("DD_ENV") ?? "dev";
        var ddVersion = Environment.GetEnvironmentVariable("DD_VERSION") ?? "latest";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = ddEnv,
                    ["service.version"] = ddVersion,
                    ["source"] = "otel",
                }))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // GenAI span sources — same names as agent-api-dotnet, since
                // both come from the same Microsoft.Extensions.AI /
                // Microsoft.Agents.AI packages.
                .AddSource("Experimental.Microsoft.Extensions.AI")
                .AddSource(ActivitySourceName)
                .SetSampler(new AlwaysOnSampler())
                // No configure delegate — see class doc comment. Reads
                // OTEL_EXPORTER_OTLP_ENDPOINT / _PROTOCOL / _HEADERS from
                // the environment per the OTel spec.
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());

        Console.WriteLine(
            "[otel] tracing sources: AspNetCore, Http, " +
            "Experimental.Microsoft.Extensions.AI, " + ActivitySourceName +
            " | exporter endpoint/protocol read from OTEL_EXPORTER_OTLP_* env vars");
    }
}
