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

    // Must be rooted for the process lifetime — an unreferenced EventListener
    // is eligible for GC, which silently stops the diagnostics stream.
    private static OtelDiagnosticsListener? _diagnosticsListener;

    public static void Configure(WebApplicationBuilder builder)
    {
        if (Environment.GetEnvironmentVariable("OTEL_TRACE_DEBUG") == "true")
        {
            _diagnosticsListener = new OtelDiagnosticsListener();
            Console.WriteLine("[otel] OTEL_TRACE_DEBUG=true — verbose OpenTelemetry SDK diagnostics enabled (see OtelDiagnosticsListener.cs)");
        }

        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            ?? "aca-agentic-poc-dotnet";
        var ddEnv = Environment.GetEnvironmentVariable("DD_ENV") ?? "dev";
        var ddVersion = Environment.GetEnvironmentVariable("DD_VERSION") ?? "latest";

        // See class doc comment above for why this fallback exists.
        var explicitOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var isGrpc = (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL") ?? "grpc") == "grpc";
        var tracesEndpoint = NormalizeEndpoint(
            explicitOtlpEndpoint ?? Environment.GetEnvironmentVariable("CONTAINERAPP_OTEL_TRACING_GRPC_ENDPOINT"),
            isGrpc, "/v1/traces");
        var metricsEndpoint = NormalizeEndpoint(
            explicitOtlpEndpoint ?? Environment.GetEnvironmentVariable("CONTAINERAPP_OTEL_METRIC_GRPC_ENDPOINT"),
            isGrpc, "/v1/metrics");

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

    // Setting AddOtlpExporter's o.Endpoint explicitly in code (required here
    // since one binary must pick between two different endpoint sources —
    // see class doc comment) bypasses the SDK's normal env-var-driven
    // per-signal path handling entirely, for BOTH protocols:
    //
    //   - gRPC: channel targets must be bare scheme+host+port — the exporter
    //     appends the fixed gRPC service path itself
    //     (/opentelemetry.proto.collector.trace.v1.TraceService/Export).
    //     Azure's CONTAINERAPP_OTEL_TRACING_GRPC_ENDPOINT/_METRIC_GRPC_ENDPOINT
    //     vars include a "/v1/traces"-style path suffix, which — left in
    //     place — produced a doubled path the managed collector rejected as
    //     unknown ("Unimplemented", "unknown service v1/traces/...
    //     TraceService"), silently dropping every export.
    //   - HTTP/protobuf: normally the SDK auto-appends /v1/traces or
    //     /v1/metrics when OTEL_EXPORTER_OTLP_ENDPOINT is read directly from
    //     env, but that auto-suffixing does NOT happen once o.Endpoint is set
    //     explicitly in code — every request went to the bare
    //     http://localhost:4318/ instead, which the sidecar's OTLP receiver
    //     404'd (it only serves /v1/traces and /v1/metrics).
    //
    // Both confirmed via the OTel SDK diagnostics this class wires up
    // (OTEL_TRACE_DEBUG=true).
    private static string? NormalizeEndpoint(string? endpoint, bool isGrpc, string httpPathSuffix)
    {
        if (endpoint is null) return null;
        if (isGrpc)
        {
            var uri = new Uri(endpoint);
            return $"{uri.Scheme}://{uri.Authority}";
        }
        return endpoint.TrimEnd('/') + httpPathSuffix;
    }
}
