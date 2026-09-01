---
title: Follow application traces
description: Understand span ownership, log and database joins, and the boundaries between Python ddtrace and .NET OpenTelemetry
docType: guide
audience:
  - application-developer
  - observability-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 1
  label: APM & tracing
---

APM records the application work surrounding an agent run: HTTP requests, Redis and PostgreSQL access, Kafka operations, MCP calls, provider requests, and ingestion Function executions. Agent Observability adds AI-specific meaning; it does not replace this application trace.

## Know who creates each span

| Runtime | Automatic coverage | Explicit coverage | Export path |
|---|---|---|---|
| Python services | FastAPI, HTTP clients, Redis, PostgreSQL, Kafka, supported AI libraries | Load-generator run, Blob upload, and application orchestration | `ddtrace` through Datadog Agent |
| .NET services | ASP.NET Core, HTTP clients, Npgsql, Microsoft AI decorators | Redis, agent-specific tasks, security HTTP checks, and project operations | OpenTelemetry OTLP HTTP through Datadog Agent |
| Ingestion Functions (`services/adf-functions`) | HTTP, Blob, Azure AI Search, OpenAI (`ddtrace.auto`) | Embedding spans (LLM Observability) | `ddtrace` agentless (`datadog-serverless-compat`) — no Agent sidecar on Consumption plan |

Do not infer coverage from a package reference. Verify that the instrumentation is registered, the operation executes, export succeeds, and the span is classified as expected.

The .NET AI path sets `EnableSensitiveData=false`. Model and agent spans should retain timing and bounded metadata without prompt, response, or attachment content.

## Correlate structured logs

Logs emitted while a span is active include `dd.trace_id` and `dd.span_id`. Python uses Datadog log injection; .NET uses a Serilog enricher that converts W3C activity identifiers to Datadog-compatible decimal fields.

Verify correlation from the trace's Logs pivot. A log in the same time window is not sufficient—it must carry the matching trace identity and service context.

## Correlate PostgreSQL work

Python database integrations use full DBM propagation where configured. .NET emits Npgsql spans and the collector applies the DBM attributes expected by Datadog. The PostgreSQL integration also needs stable database host identity.

To validate the join, start from a request that saves or loads a conversation, open its SQL span, and follow the DBM pivot. If it is empty, inspect statement capture, propagation mode, collector processing, and reported hostname independently.

## Preserve source identity

Deployed .NET projects embed Source Link metadata and retain portable symbols. Datadog's repository integration and matching release commit are also required before a stack frame can open the correct source. Mobile symbol upload is a separate release workflow for R8 mappings and dSYMs.

## Keep health probes out of request analysis

Workloads expose:

- `/livez` for shallow process health;
- `/readyz` for cached readiness state;
- `/health` for human diagnostics and older consumers.

Kubernetes probes use the shallow routes. Python sampling rules and .NET ASP.NET Core filters exclude the verified liveness/readiness resource names before export. This reduces ingest volume without hiding login, query, model, tool, media, conversation, or error traffic.

After a tracer upgrade, confirm route resource names on a canary before broadening an exclusion. A retention filter changes indexing; it does not prevent span ingestion.

## Investigation exercise

1. Submit a request that calls an MCP tool and persists a conversation.
2. Locate the root HTTP trace and identify the slowest service boundary.
3. Follow one matching log by trace ID.
4. Follow one PostgreSQL span into DBM.
5. Confirm the model or agent span omits sensitive content.
6. Verify liveness/readiness traffic is absent while the real request remains complete.

Continue to [Agent trace investigation](/infra-advisor-ai/llm-engineering/monitoring/spans-and-traces/) for the AI-specific tree or [App and API Protection](../app-api-protection/) for the .NET dual-runtime boundary.
