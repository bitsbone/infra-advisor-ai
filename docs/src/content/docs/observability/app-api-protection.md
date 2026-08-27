---
title: App & API Protection
description: Service-level Datadog App and API Protection for Python and .NET with an OpenTelemetry-only APM path
---

InfraAdvisor enables Datadog App and API Protection on both public Agent API implementations. Python keeps its existing Datadog APM instrumentation, while .NET loads the Datadog security profiler but disables its APM span production so the application-owned OpenTelemetry SDK remains the only source of .NET traces.

## Selected architecture

```text
mobile or browser
       |
       v
ui pod: nginx reverse proxy on an Azure LoadBalancer
       | /api/*                         | /api-dotnet/*
       v                                v
Python Agent API                    .NET Agent API
ddtrace APM + AAP                   Datadog AAP profiler
       |                                + DD_APM_TRACING_ENABLED=false
       |                                + OpenTelemetry SDK -> OTLP
       +----------> Datadog Agent <-----+
```

This is deliberately a service-level design after evaluating both tracer and gateway implementations. The current public edge is an application-owned `nginx:1.27-alpine` container in the UI pod, not ingress-nginx, Istio, Envoy Gateway, or a Kubernetes Gateway API implementation. Datadog's automatic Kubernetes gateway protection cannot configure that custom reverse proxy as-is. A Datadog NGINX module could be built into a replacement UI image, but the integration is experimental, requires a version-matched AppSec module and an NGINX build with threads, and does not currently provide API Security. In-process protection is therefore the smallest option that satisfies both threat protection and API discovery without replacing the cluster edge.

## Configuration pattern

The Kubernetes pod templates opt in to the Datadog Admission Controller with `admission.datadoghq.com/enabled: "true"` so Agent connection settings and unified tags are available. The cluster keeps `mutateUnlabelled: false`, and its Single Step Instrumentation target also requires the exact `app: agent-api-dotnet` pod label. Only the .NET API receives an injected library; the Python image's packaged and pinned tracer remains authoritative. The .NET pod and target pin `admission.datadoghq.com/dotnet-lib.version: "3.44.0"`, the minimum .NET tracer version with the complete documented API Security capability, instead of using an unbounded `latest` image.

Python already packages and initializes a pinned `ddtrace` 4.x release. Its ConfigMap makes the security contract explicit:

```yaml
DD_TRACE_ENABLED: "true"
DD_APPSEC_ENABLED: "true"
DD_API_SECURITY_ENABLED: "true"
```

.NET continues to export application spans through `OpenTelemetry.Exporter.OpenTelemetryProtocol`. The injected Datadog profiler runs the security engine with this separate contract:

```yaml
DD_APPSEC_ENABLED: "true"
DD_API_SECURITY_ENABLED: "true"
DD_APM_TRACING_ENABLED: "false"
OTEL_EXPORTER_OTLP_ENDPOINT: "http://datadog-agent.datadog.svc.cluster.local:4318"
```

`DD_APM_TRACING_ENABLED=false` is intentional and is not interchangeable with `DD_TRACE_ENABLED=false`. The standalone App and API Protection setting keeps the security runtime active and limits Datadog tracer output to the minimum security telemetry required by the product. It does not disable the application's OpenTelemetry SDK or OTLP exporter. Some security traces are therefore expected even though ordinary .NET APM spans come only from OpenTelemetry.

Reference implementations:

- [Python Deployment](https://github.com/kyletaylored/infra-advisor-ai/blob/main/k8s/agent-api/deployment.yaml) and [Python ConfigMap](https://github.com/kyletaylored/infra-advisor-ai/blob/main/k8s/agent-api/configmap.yaml)
- [.NET Deployment](https://github.com/kyletaylored/infra-advisor-ai/blob/main/k8s/agent-api-dotnet/deployment.yaml) and [.NET ConfigMap](https://github.com/kyletaylored/infra-advisor-ai/blob/main/k8s/agent-api-dotnet/configmap.yaml)
- [Datadog Agent configuration](https://github.com/kyletaylored/infra-advisor-ai/blob/main/datadog/datadog-agent.yaml)
- [Executable deployment contract tests](https://github.com/kyletaylored/infra-advisor-ai/blob/main/services/agent-api/tests/test_appsec_kubernetes_contract.py)

## Tracer versus gateway decision

| Option | Detection and blocking | API Security | Repository impact | Decision |
|--------|------------------------|--------------|-------------------|----------|
| Python and .NET security libraries | Supported at the application request boundary | Supported with the pinned language libraries | Adds one security runtime per service; .NET keeps `DD_APM_TRACING_ENABLED=false` so OpenTelemetry remains the application trace source | Selected baseline |
| Datadog module in the existing custom NGINX image | Experimental threat detection and blocking | Not supported by the NGINX integration | Requires replacing the stock Alpine image with a version- and architecture-matched module build, configuring WAF thread pools, connecting it to the Agent, and maintaining that custom image | Rejected as the only layer because it cannot provide API Security |
| ingress-nginx controller | Supported edge inspection and blocking; automated Cluster Agent injection is the recommended setup | Not supported by the NGINX integration | Replaces the UI pod's application-owned reverse proxy and LoadBalancer routing model; requires Cluster Agent and Helm chart version upgrades | Useful future edge protection, but not a gateway-only replacement for the requested API Security coverage |
| Envoy Gateway | Detection, blocking, and custom block responses through the Datadog external processor | Supported | Introduces Envoy Gateway, Gateway API resources, a security processor deployment, Remote Configuration, capacity planning, and failure-mode policy; integration is currently Preview | Strongest gateway-only candidate after an explicit edge migration |
| Generic Kubernetes Gateway API request mirror | Detection on mirrored requests only | Endpoint discovery is available, but response inspection and blocking are not | Requires Gateway API CRDs and a compatible controller; the Datadog integration is experimental and only analyzes JSON request bodies | Evaluation-only, not a protection replacement |
| Hybrid gateway plus service libraries | Earliest edge blocking plus code-aware service context | Supported at the service layer | Highest operational cost and two rollout surfaces; duplicate observations must be measured | Revisit only if defense in depth is a stated requirement |

A gateway-first implementation becomes attractive if the application deliberately moves public routing to ingress-nginx or Envoy Gateway. At that point, terminate `/auth`, `/api`, and `/api-dotnet` traffic at the supported gateway, enable the documented Datadog module or external security processor, and validate fail-open or fail-closed behavior, SSE streaming, WebSocket upgrades if introduced, authentication redirects, request-body limits, 10 MB media uploads, response inspection, and latency before removing the service libraries. The gateway option is an infrastructure migration rather than an environment-variable substitution.

If Envoy Gateway becomes the selected edge, ordinary application APM remains independent of the security processor. The .NET service can remove Datadog profiler injection entirely and continue exporting application spans only through OpenTelemetry; the external processor can use `DD_APM_TRACING_ENABLED=false` to emit only the minimum security transport data. Gateway-only protection intentionally gives up in-process capabilities such as Exploit Prevention, runtime activation, runtime SCA, and IAST, so that reduced coverage must be explicitly accepted rather than inferred from a successful edge canary. Until that migration is implemented and accepted, removing the .NET security profiler would leave the .NET API without the requested protection.

See Datadog's [setup option index](https://docs.datadoghq.com/security/application_security/setup/), [Python Kubernetes setup](https://docs.datadoghq.com/security/application_security/setup/python/kubernetes/), [.NET Kubernetes setup](https://docs.datadoghq.com/security/application_security/setup/dotnet/kubernetes/), [ingress-nginx setup](https://docs.datadoghq.com/security/application_security/setup/nginx/ingress-controller/), [Envoy Gateway setup](https://docs.datadoghq.com/security/application_security/setup/kubernetes/envoy-gateway/), [Gateway API request-mirror setup](https://docs.datadoghq.com/security/application_security/setup/kubernetes/gateway-api/), [standalone AAP behavior](https://docs.datadoghq.com/security/application_security/guide/standalone_application_security/), and [compatibility matrix](https://docs.datadoghq.com/security/application_security/setup/compatibility/) for the current platform capabilities and prerequisites.

## Security and privacy boundary

App and API Protection inspects request context in the application process to detect attacks and discover API structure. Do not add passwords, JWTs, prompts, model responses, attachment bodies, SAS query strings, provider payloads, or API keys as custom span tags, logs, or security attributes. API Security is used to understand endpoint shape and risk; it is not permission to expand telemetry payload capture.

The RUM and mobile clients continue to mask sensitive inputs. Authentication credentials and JWTs remain memory-only. Kubernetes secrets continue to provide privileged values, and none of the AAP configuration files contain a Datadog API key or application key.

## Rollout and verification

1. Build and deploy both Agent API images and apply the updated ConfigMaps and Deployments. A pod restart is required because profiler injection occurs at process startup.
2. Confirm the Datadog Cluster Agent Admission Controller is healthy and that each new pod has the expected Datadog library init container and injected volumes/environment.
3. Send ordinary authenticated traffic through `/api/query` and `/api-dotnet/query` and confirm both services report AAP coverage and API endpoints in Datadog.
4. Confirm Python APM traces remain unchanged. For .NET, confirm OpenTelemetry traces still arrive through OTLP and that no parallel Datadog-profiler APM trace tree appears.
5. Use Datadog's documented harmless validation traffic in a controlled environment and confirm a security signal. Do not probe third-party systems or production endpoints without authorization.
6. Start in monitoring mode. Enable blocking through Remote Configuration only after reviewing false positives, endpoint coverage, latency, and the rollback path.

Rollback is workload-scoped: set `DD_APPSEC_ENABLED=false`, disable or remove the workload's Remote Configuration activation state, restore the prior .NET admission opt-out if the profiler itself must be removed, and restart the affected Deployment. Removing the local variable is not deterministic because Remote Configuration could activate protection again. Do not disable the cluster-wide Datadog Agent or OpenTelemetry collector to roll back one service.

## Automated guardrails

`test_appsec_kubernetes_contract.py` rejects duplicate YAML keys, verifies both services explicitly enable App and API Protection, limits Single Step Instrumentation to the `.NET` API pod selector, preserves the packaged Python tracer boundary, and locks in the .NET `DD_APM_TRACING_ENABLED=false` setting while requiring the OTLP exporter to remain configured. This catches the most damaging regressions for this design: silently dropping a duplicated Kubernetes `env` block, injecting libraries across the namespace, replacing the pinned Python tracer, or accidentally creating a second .NET APM trace pipeline.
