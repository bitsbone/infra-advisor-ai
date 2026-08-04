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
//   - aca-agentic-poc-managed: ACA's platform-managed OTel agent does NOT
//     inject the standard OTEL_EXPORTER_OTLP_ENDPOINT — verified empirically
//     against a live deployment (az containerapp exec + `export` inside the
//     container). It instead injects Azure-specific per-signal vars
//     (CONTAINERAPP_OTEL_TRACING_GRPC_ENDPOINT,
//     CONTAINERAPP_OTEL_METRIC_GRPC_ENDPOINT), which standard OTel SDKs have
//     no built-in knowledge of.
//   - aca-agentic-poc-sidecar: Bicep explicitly sets
//     OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 (HTTP/protobuf,
//     the datadog-sidecar container's OTLP receiver).
//
// Since the exact same container image runs both, Configure() below prefers
// the standard OTEL_EXPORTER_OTLP_ENDPOINT when explicitly set (the sidecar
// path always sets it), and falls back to the Azure-specific
// CONTAINERAPP_OTEL_*_GRPC_ENDPOINT vars otherwise (only present/needed for
// the managed path) — one shared code path, no per-deployment branching.
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

        // See class doc comment above for why this fallback exists.
        var explicitOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var tracesEndpoint = explicitOtlpEndpoint
            ?? Environment.GetEnvironmentVariable("CONTAINERAPP_OTEL_TRACING_GRPC_ENDPOINT");
        var metricsEndpoint = explicitOtlpEndpoint
            ?? Environment.GetEnvironmentVariable("CONTAINERAPP_OTEL_METRIC_GRPC_ENDPOINT");

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
                .AddOtlpExporter(o =>
                {
                    if (tracesEndpoint is not null) o.Endpoint = new Uri(tracesEndpoint);
                }))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o =>
                {
                    if (metricsEndpoint is not null) o.Endpoint = new Uri(metricsEndpoint);
                }));

        Console.WriteLine(
            "[otel] tracing sources: AspNetCore, Http, " +
            "Experimental.Microsoft.Extensions.AI, " + ActivitySourceName +
            " | traces endpoint: " + (tracesEndpoint ?? "(SDK default)") +
            " | metrics endpoint: " + (metricsEndpoint ?? "(SDK default)"));
    }
}
