---
title: Compare OTel export paths on Azure Container Apps
description: A verified experiment comparing Azure's managed collector with a Datadog sidecar for the same .NET application
docType: experiment
audience:
  - platform-engineer
  - observability-engineer
maturity: stable
verifiedOn: 2026-08-27
learning:
  estimatedMinutes: 15
  objectives:
    - Explain how collector placement changes telemetry ownership and failure modes
    - Configure the OTLP protocol and endpoint shape required by each path
    - Use OpenTelemetry diagnostics to distinguish application success from export success
sidebar:
  label: OTel on Container Apps
---

The optional `aca-agentic-poc-dotnet` service runs the same .NET application twice on Azure Container Apps (ACA). One deployment exports through ACA's managed OpenTelemetry collector; the other exports to a Datadog `serverless-init` sidecar. Both paths produce complete Datadog traces, but the experiment exposes different ownership and failure modes.

## What changes between deployments

| Dimension | ACA managed collector | Datadog sidecar |
|---|---|---|
| Collector location | Container Apps Environment | Same app revision |
| App containers | One | App plus sidecar |
| Export protocol used here | gRPC | HTTP/protobuf |
| Endpoint source | ACA-specific injected variable | Explicit `http://localhost:4318` |
| Datadog configuration | Environment destination | Sidecar environment |
| Primary debugging surface | OTel SDK diagnostics plus platform logs | OTel SDK and sidecar logs |

The sidecar adds a container but keeps the collection boundary close to the application. The managed path removes that container while making the Azure platform part of endpoint discovery and troubleshooting.

## Managed path: normalize Azure's endpoint

ACA injects per-signal variables such as `CONTAINERAPP_OTEL_TRACING_GRPC_ENDPOINT`; it does not populate the standard `OTEL_EXPORTER_OTLP_ENDPOINT` assumed by many SDK examples. The application therefore falls back to the ACA variable.

The injected gRPC value can include `/v1/traces`. A gRPC exporter needs only `scheme://host:port` because it adds its own service path. Passing the suffix through produces a doubled path and an `Unimplemented` export failure.

```csharp
static string NormalizeGrpcEndpoint(string endpoint)
{
    var uri = new Uri(endpoint);
    return $"{uri.Scheme}://{uri.Authority}";
}
```

This lesson is broader than ACA: endpoint syntax is part of the protocol contract, not an interchangeable string.

## Sidecar path: name the protocol and signal path

The application targets the sidecar over the revision's local network:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

The .NET exporter defaults to gRPC if the protocol is omitted. Pointing that default at the HTTP receiver silently loses telemetry. A second subtlety appears when code assigns the exporter's `Endpoint` property: the SDK no longer derives the per-signal HTTP path in the same way it does from environment configuration. The shared setup therefore appends `/v1/traces` or `/v1/metrics` for HTTP/protobuf while stripping paths for gRPC.

```csharp
static string NormalizeEndpoint(string endpoint, bool grpc, string signalPath)
{
    var uri = new Uri(endpoint);
    return grpc
        ? $"{uri.Scheme}://{uri.Authority}"
        : endpoint.TrimEnd('/') + signalPath;
}
```

The complete logic lives in `services/aca-agentic-poc-dotnet/Observability/TelemetrySetup.cs`.

## Make silent exporter failures visible

Neither broken path produced useful application errors. The proof of cause came from an `EventListener` that forwards OpenTelemetry `EventSource` events to stdout when `OTEL_TRACE_DEBUG=true`.

Keep the listener rooted for the process lifetime; otherwise garbage collection can stop diagnostics. Enable it only during investigation because exporter diagnostics are verbose and may increase log volume.

## Run the comparison

1. Deploy the opt-in ACA module with one immutable application image.
2. Send the same tool-using request to both deployments.
3. Confirm each trace contains the HTTP request, agent invocation, model calls, and tool operation.
4. Compare service identity and export behavior before comparing latency.
5. Break one endpoint or protocol setting in a disposable environment and use OTel diagnostics to explain the absence of spans.

The learning outcome is not “sidecar always wins.” It is the ability to locate responsibility across application instrumentation, OTLP protocol, collector placement, and Datadog ingestion.

## Operational boundaries

- Use immutable image tags or a changing revision suffix; updating the same `:latest` reference may not create a new ACA revision.
- Keep the Datadog API key in the collector boundary, not application code.
- Preserve W3C trace context from browser RUM so the OTel ASP.NET Core root joins the frontend request.
- Suppress high-frequency no-op spans, such as empty polls, before they obscure meaningful work.

See [Azure infrastructure](../infrastructure/) for the opt-in module and [Datadog's OTLP ingestion guide](https://docs.datadoghq.com/opentelemetry/setup/agent/otlp_ingest/) for current receiver configuration.
