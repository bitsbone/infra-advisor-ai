---
title: "OTel on Azure Container Apps: Managed Agent vs. Datadog Sidecar"
description: A working, verified reference for instrumenting a .NET agentic app on ACA with OpenTelemetry and Datadog, covering both integration paths and the real bugs each one hides.
---

This is a reference for anyone wiring OpenTelemetry into a .NET app on Azure Container Apps (ACA) with Datadog as the backend. It's derived from a live, working proof-of-concept in this repo (`services/aca-agentic-poc-dotnet/`, deployed as `aca-agentic-poc-managed` and `aca-agentic-poc-sidecar` — see [Azure Infrastructure](/architecture/infrastructure/#aca-agentic-poc-aca-agentic-pocbicep) for the Bicep/deployment side). Both paths are confirmed producing full trace trees in Datadog, including the specific bugs that silently broke each one and how they were found.

**TL;DR**: both paths work, but the sidecar path is more reliable and far easier to debug. If you just need something working today, start there.

## The two paths

Both are legitimate, Microsoft/Datadog-documented ways to get OTel data from ACA into Datadog — but they're structurally different, and that difference is exactly where the bugs live.

| | Managed OTel agent | Datadog serverless-init sidecar |
|---|---|---|
| What it actually is | Microsoft-run generic OpenTelemetry Collector, configured with a Datadog exporter | The **Datadog Agent itself** (a lightweight build for serverless/container use), with its built-in OTLP receiver enabled |
| Where it's configured | Container Apps *Environment* level (`openTelemetryConfiguration`) | A second container (`datadog/serverless-init`) in the same revision |
| Containers per app | 1 | 2 (`app` + `datadog-sidecar`) |
| Protocol | gRPC only | HTTP/protobuf (also supports gRPC) |
| App's exporter target | Platform-injected, Azure-specific env vars (see below) | `http://localhost:4318`, set explicitly |

If you're deciding which to implement: the sidecar path is [Datadog's documented "OTLP Ingestion by the Datadog Agent"](https://docs.datadoghq.com/opentelemetry/setup/agent/otlp_ingest/) pattern, just running as an ACA sidecar instead of a standalone container — nothing generic-collector about it. The managed path is Azure's own infrastructure forwarding to Datadog via a Collector exporter, which is a different (and here, buggier) code path entirely.

## Path 1: the managed OpenTelemetry agent

Enabled once per Container Apps Environment in Bicep:

```bicep
properties: {
  openTelemetryConfiguration: {
    destinationsConfiguration: {
      dataDogConfiguration: { site: datadogSite, key: datadogApiKey }
    }
    tracesConfiguration: { destinations: ['dataDog'] }
    logsConfiguration: { destinations: ['dataDog'] }
    metricsConfiguration: { destinations: ['dataDog'] }
  }
}
```

### Gotcha 1 — the platform does NOT inject `OTEL_EXPORTER_OTLP_ENDPOINT`

Every OTel-on-ACA writeup (including this repo's original PRD) assumes the platform auto-injects the standard `OTEL_EXPORTER_OTLP_ENDPOINT` env var once `openTelemetryConfiguration` is set. **It doesn't.** Verified by `export`-ing the actual environment inside a running container (`az containerapp exec`):

```
CONTAINERAPP_OTEL_TRACING_GRPC_ENDPOINT='http://k8se-otel.k8se-apps.svc.cluster.local:4317/v1/traces'
CONTAINERAPP_OTEL_METRIC_GRPC_ENDPOINT='http://k8se-otel.k8se-apps.svc.cluster.local:4317/v1/metrics'
CONTAINERAPP_OTEL_LOGGING_GRPC_ENDPOINT='http://k8se-otel.k8se-apps.svc.cluster.local:4317/v1/logs'
```

Azure injects its own, Azure-specific, per-signal env vars instead — which no standard OTel SDK knows to read. Your app has to fall back to these explicitly when the standard var is absent.

### Gotcha 2 — those endpoint values include a path suffix that breaks gRPC

Notice the `/v1/traces` suffix above. That's an HTTP/protobuf convention. gRPC channel targets must be **bare `scheme://host:port`** — the gRPC exporter appends the fixed service path itself (`/opentelemetry.proto.collector.trace.v1.TraceService/Export`). Pass the suffixed URI straight through and the real request path becomes `/v1/traces/opentelemetry.proto.collector.trace.v1.TraceService/Export`, which the collector correctly rejects:

```
OpenTelemetry-Exporter-OpenTelemetryProtocol/ExportFailure: Export failed for
http://k8se-otel.k8se-apps.svc.cluster.local:4317/v1/traces/opentelemetry.proto.collector.trace.v1.TraceService/Export.
Status(StatusCode="Unimplemented", Detail="unknown service v1/traces/opentelemetry.proto.collector.trace.v1.TraceService")
```

This failed **silently** in every other diagnostic surface (console, `az containerapp logs show`, Datadog itself just showed nothing) until the OTel SDK's own internal diagnostics were wired up — see [Debugging](#debugging-otel-export-failures) below. The fix is to strip the path when constructing the gRPC channel URI.

## Path 2: the Datadog serverless-init sidecar

A second container in the same revision, with its OTLP receiver enabled the same way you'd enable it on any Datadog Agent:

```bicep
{
  name: 'datadog-sidecar'
  image: 'index.docker.io/datadog/serverless-init:latest'
  env: [
    { name: 'DD_API_KEY', secretRef: 'datadog-api-key' }
    { name: 'DD_SITE', value: datadogSite }
    { name: 'DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_GRPC_ENDPOINT', value: '0.0.0.0:4317' }
    { name: 'DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_HTTP_ENDPOINT', value: '0.0.0.0:4318' }
    { name: 'DD_OTLP_CONFIG_TRACES_ENABLED', value: 'true' }
  ]
}
```

The app container talks to it over `localhost` (same revision, same network namespace):

```bicep
{ name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: 'http://localhost:4318' }
{ name: 'OTEL_EXPORTER_OTLP_PROTOCOL', value: 'http/protobuf' }
```

### Gotcha 1 — the .NET OTLP exporter defaults to gRPC

If you don't set `OTEL_EXPORTER_OTLP_PROTOCOL` explicitly, the .NET SDK defaults to `grpc` per the OTel spec — regardless of what port you point it at. Pointing at the sidecar's HTTP port (4318) without setting the protocol means the exporter tries to gRPC-handshake against an HTTP/protobuf listener and drops every span, again with **zero error output anywhere**.

### Gotcha 2 — setting the exporter's `Endpoint` in code skips auto-path-suffixing

If your app needs to support *both* paths from one binary (see below), you'll end up setting the OTLP exporter's `Endpoint` property explicitly in code rather than letting the SDK read `OTEL_EXPORTER_OTLP_ENDPOINT` from the environment itself. That has a side effect: the SDK's normal behavior of auto-appending `/v1/traces` / `/v1/metrics` to a bare endpoint **only happens when it reads the env var itself** — setting `.Endpoint` in code bypasses it entirely. Every request went to the literal `http://localhost:4318/` and got a 404 from the receiver (which only serves `/v1/traces` and `/v1/metrics`):

```
OpenTelemetry-Exporter-OpenTelemetryProtocol/HttpRequestFailed: HTTP request to
http://localhost:4318/ failed. Response: 404 page not found
```

## Supporting both paths from one binary

If the point is to compare the two paths (or just to keep application code identical across environments), the exporter setup needs to pick its endpoint at runtime rather than assume one path:

```csharp
var explicitOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
var isGrpc = (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL") ?? "grpc") == "grpc";

var tracesEndpoint = NormalizeEndpoint(
    explicitOtlpEndpoint ?? Environment.GetEnvironmentVariable("CONTAINERAPP_OTEL_TRACING_GRPC_ENDPOINT"),
    isGrpc, "/v1/traces");

// gRPC: strip any path, leaving bare scheme://host:port.
// HTTP/protobuf: append the standard per-signal path if it's not already there.
static string? NormalizeEndpoint(string? endpoint, bool isGrpc, string httpPathSuffix)
{
    if (endpoint is null) return null;
    if (isGrpc)
    {
        var uri = new Uri(endpoint);
        return $"{uri.Scheme}://{uri.Authority}";
    }
    return endpoint.TrimEnd('/') + httpPathSuffix;
}
```

Full working version: `services/aca-agentic-poc-dotnet/Observability/TelemetrySetup.cs`.

## Debugging OTel export failures

Both bugs above produced **no error anywhere visible** — not in application console output, not in `az containerapp logs show`, not as any kind of Datadog alert. The OTel .NET SDK's own internal diagnostics (span lifecycle, exporter connection/export success or failure) exist, but its built-in "self-diagnostics" feature writes to a log file on disk — impractical here, since ACA's minimal container images have no shell tools to read files with (confirmed via `az containerapp exec`: no `cat`, no `ps`, busybox `sh` only), so retrieving a log file would mean a painful, rate-limited exec session per check.

The fix: bridge the SDK's internal `EventSource`s straight to stdout, which is already visible via the platform's log stream:

```csharp
public sealed class OtelDiagnosticsListener : EventListener
{
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
            EnableEvents(eventSource, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        var message = eventData.Payload is { Count: > 0 } && eventData.Message is not null
            ? string.Format(eventData.Message, [.. eventData.Payload])
            : eventData.Message ?? eventData.EventName ?? "(no message)";
        Console.WriteLine($"[otel-diag] {eventData.EventSource.Name}/{eventData.EventName}: {message}");
    }
}
```

Instantiate it once and keep it rooted for the process lifetime (an unreferenced `EventListener` is GC-eligible, which silently stops the stream) — gate it behind an env var, since it's genuinely verbose:

```csharp
private static OtelDiagnosticsListener? _diagnosticsListener;
if (Environment.GetEnvironmentVariable("OTEL_TRACE_DEBUG") == "true")
    _diagnosticsListener = new OtelDiagnosticsListener();
```

This is what actually found both bugs above — `az containerapp logs show` immediately surfaced `ExportFailure`/`HttpRequestFailed` events with exact status codes and messages, in both cases within seconds of a test request. Full version: `services/aca-agentic-poc-dotnet/Observability/OtelDiagnosticsListener.cs`.

## RUM → APM trace correlation

If the app also serves a browser UI, Datadog RUM's `allowedTracingUrls` config controls which outgoing `fetch`/XHR calls get trace-context headers injected, connecting a RUM session to its backend trace:

```js
allowedTracingUrls: [window.location.origin],
```

Without an explicit `propagatorTypes`, the current RUM SDK sends both `datadog`-format and W3C `tracecontext` headers by default — matching what a pure-OTel .NET backend (no `dd-trace-dotnet`) expects, since ASP.NET Core's OTel instrumentation only reads the standard `traceparent` header. No extra config needed on the RUM side for this specific case.

To surface the link in your own UI, capture the request's trace ID server-side from the ASP.NET Core instrumentation's root `Activity` and return it to the client:

```csharp
var traceId = Activity.Current?.TraceId.ToHexString();
var traceUrl = traceId is not null ? $"https://{site}/apm/trace/{traceId}" : null;
```

Datadog accepts the 32-character hex W3C trace ID directly in `/apm/trace/<id>` for anything ingested via OTLP — no conversion needed.

## Operational notes

- **`:latest` tag + revision suffix.** If you pin the Container App's image to a fixed `:latest` tag for simplicity, `az containerapp update --image ...:latest` (same string every time) does **not** reliably create a new revision — ACA sees no diff in the container template and skips it, silently leaving the old image running even after a new one is pushed. Force a new revision every deploy with a changing `revisionSuffix` (a Bicep param defaulting to `utcNow('yyyyMMddHHmmss')` works well for this).
- **`HOST_PROC=/proc` on the sidecar.** Datadog Agent 7.61.0+ has a known Docker-runtime issue where the OTLP pipeline fails to start (`failed to register process metrics: process does not exist`) unless this is set. ACA's containerd/Kubernetes-like runtime has similar `/proc`-mount behavior to Docker, so this is a cheap hedge worth setting proactively.
- **Empty-poll or other high-frequency background work.** Not ACA-specific, but worth flagging alongside this: if your app has a polling loop (Kafka, queue consumers, health checks), check whether your tracing library has a knob to suppress spans for no-op iterations before it floods your trace volume — e.g. ddtrace's Kafka integration defaults to tracing every `poll()` call including empty ones (`DD_KAFKA_EMPTY_POLL_ENABLED`, default `true`).

## Working example

The full source for both paths is in this repo:

- `infra/bicep/modules/aca-agentic-poc.bicep` — both Container Apps, the shared environment, secrets wiring
- `services/aca-agentic-poc-dotnet/Observability/TelemetrySetup.cs` — the endpoint-normalization logic
- `services/aca-agentic-poc-dotnet/Observability/OtelDiagnosticsListener.cs` — the stdout diagnostics bridge
- `services/aca-agentic-poc-dotnet/Program.cs` — the agent, Basic Auth middleware, trace-link response

See [Azure Infrastructure](/architecture/infrastructure/#aca-agentic-poc-aca-agentic-pocbicep) for deployment details and current status.
