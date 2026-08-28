---
title: Protect the public Agent APIs
description: Understand why Python and .NET use service-level Datadog protection while .NET retains one OpenTelemetry APM path
docType: concept
audience:
  - security-engineer
  - platform-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 5
  label: App & API Protection
---

InfraAdvisor enables Datadog App and API Protection inside both public Agent APIs. Python uses its packaged Datadog tracer for APM and security. .NET loads the Datadog security profiler but keeps the application trace tree in OpenTelemetry.

## Selected boundary

```text
public UI proxy
   ├─ /api/* ────────▶ Python API: ddtrace APM + security
   └─ /api-dotnet/* ─▶ .NET API: Datadog security runtime
                                 + DD_APM_TRACING_ENABLED=false
                                 + application OpenTelemetry → OTLP
```

`DD_APM_TRACING_ENABLED=false` is the crucial .NET boundary. It prevents the injected profiler from creating a parallel application trace tree without disabling its standalone security runtime or the application's OTel SDK. Some minimum security transport telemetry can still be expected.

## Deployment contract

Both services explicitly set:

```text
DD_APPSEC_ENABLED=true
DD_API_SECURITY_ENABLED=true
```

Python owns its pinned `ddtrace` dependency. The Admission Controller target selects only the `.NET` Agent API pod and pins the injected .NET library version. `mutateUnlabelled=false` prevents namespace-wide surprise injection. The executable contract test in `services/agent-api/tests/test_appsec_kubernetes_contract.py` verifies these invariants and rejects duplicate YAML keys.

## Why protection is in process

The current public edge is an application-owned nginx container, not a supported ingress controller or Envoy Gateway deployment. Moving protection to the edge would therefore be an infrastructure migration, not an environment-variable change.

| Option | Benefit | Cost or gap | Current decision |
|---|---|---|---|
| Service libraries | Request-aware protection and API discovery | Runtime in each public API | Selected |
| Module in current nginx image | Earlier inspection | Custom image and capability gaps | Not sufficient alone |
| Supported ingress or Envoy gateway | Central edge policy | New routing, capacity, failure, and streaming behavior | Future architecture decision |
| Hybrid edge plus service | Defense in depth | Duplicate rollout and evidence | Only with an explicit requirement |

Check Datadog's current [App and API Protection setup options](https://docs.datadoghq.com/security/application_security/setup/) and compatibility documentation before changing the boundary; gateway and language support evolve.

## Privacy boundary

Protection inspects request context. It does not authorize copying passwords, JWTs, prompts, model responses, media, signed URLs, or provider payloads into custom tags or logs. Browser and mobile masking, Kubernetes secrets, metadata-only AI telemetry, and provider normalization remain separate controls.

## Verify and roll back safely

1. Restart the workload so profiler injection occurs at process start.
2. Confirm the expected init container, volumes, and environment on the selected pod only.
3. Send ordinary authenticated traffic to each backend and verify endpoint discovery and coverage.
4. Confirm .NET application traces still arrive through OTLP without a duplicate profiler tree.
5. Use only Datadog's harmless validation traffic in an authorized environment.
6. Begin in monitoring mode and review false positives and latency before blocking.

Rollback is workload-scoped: disable AppSec for the affected service, remove any Remote Configuration activation, and restart it. Do not disable the cluster Agent or OTLP collector to roll back one workload.
