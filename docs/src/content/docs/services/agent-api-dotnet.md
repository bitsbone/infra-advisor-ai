---
title: Agent API (.NET)
description: Understand the Microsoft agent, OpenTelemetry, response-evaluator, and resilient MCP implementation
docType: reference
audience:
  - application-developer
  - observability-engineer
maturity: stable
verifiedOn: 2026-09-09
sidebar:
  order: 2
---

The .NET Agent API exposes the same user-facing jobs as Python through a different framework. ASP.NET Core hosts a Microsoft Agent Framework router + 5 tool-partitioned specialist agents (`Services/SpecialistRegistry.cs`), wired via `Microsoft.Agents.AI.Workflows`' `HandoffWorkflowBuilder`, alongside Microsoft.Extensions.AI decorators, project retrieval, a cached MCP client, and OpenTelemetry export.

## Request path

```text
authenticated request
  → validate conversation and attachment ownership
  → AI Guard HTTP pre-flight
  → transcribe audio / prepare image content
  → deterministic domain classification
  → retrieve project context
  → the router hands off to one tool-partitioned specialist, which reasons over its allowed MCP tools
  → extract sources and artifacts
  → persist and stream/return result
  → sample registered response evaluators
```

This now matches Python's router/specialist topology, including the same registry `prompt_id`s (`router`, `specialist-<domain>`) — see [.NET and Python parity](/infra-advisor-ai/development/dotnet-python-parity/) and [prompt tracking](/infra-advisor-ai/llm-engineering/monitoring/prompt-tracking/) for how the two converged.

## Client contract

The service implements query, streaming, media, models, tools, suggestions, feedback, conversations, session cleanup, and health endpoints equivalent to the Python surface. It adds two read-only diagnostic routes:

- `GET /eval/status` reports evaluator registration and recent Datadog submissions.
- `GET /ai-guard/status` reports the HTTP guard client's enabled state and recent outcomes.

Use the running endpoint metadata and shared client models for exact fields. Both query modes transport the same additive artifact and attachment contracts.

New conversations default to `gpt-5.4-mini`. `AVAILABLE_MODELS` controls the ordered selector options, and an explicitly saved model remains attached to its conversation.

## Stateful boundaries

`ConversationService` verifies JWT ownership before a conversation influences agent state, then repeats the owner predicate during writes. Redis stores conversation history as a plain `List<ChatMessage>` (`AgentSessionStore`) under an opaque tenant/session hash — text-only, matching Python's own history reconstruction exactly (no tool-call replay). This replaced MAF's per-agent `AgentSession` serialization once the router+specialist redesign meant no single agent could represent "the conversation" anymore, and a `HandoffWorkflowBuilder` workflow has no cross-request persistence surface of its own.

`McpClientHolder` caches the .NET MCP session. If a session-expired failure occurs before unsafe response progress, the service serializes a refresh and retries once. `AgentHolder` (one instance per specialist, held by `SpecialistRegistry`) rebuilds its agent when the tool-list or prompt generation changes, and additionally supports a per-request, per-user-targeted rebuild (`GetAgentForRequestAsync`) for [Feature Flag-targeted prompt versions](/infra-advisor-ai/llm-engineering/monitoring/prompt-targeting/). Python does not need the same holder because its adapter creates a fresh client lifecycle per request.

## Evaluation pipeline

At `EVAL_SAMPLE_RATE`, the service runs five `IResponseEvaluator` implementations after the response:

- citation presence;
- business-development tool ordering;
- tool-routing accuracy;
- M.E.AI relevance;
- M.E.AI groundedness.

`DatadogEvalsClient` submits results against the original agent span and records recent outcomes. Evaluations are fire-and-forget; they must not delay or fail the user's answer.

## Telemetry and security

The application emits OpenTelemetry through OTLP HTTP to the Datadog Agent. ASP.NET Core, HTTP, Npgsql, and Microsoft AI instrumentation combine with explicit project activities. `TelemetryPrivacy.EnableSensitiveData=false` keeps message and attachment content out of generated AI spans.

The Datadog Admission Controller injects a pinned .NET profiler for App and API Protection. `DD_APM_TRACING_ENABLED=false` prevents that profiler from creating a second application trace tree; OpenTelemetry remains the APM source.

## Verify a change

Run the .NET Agent API test project in Release configuration. Use a disposable PostgreSQL database for ownership, message, and JSONB artifact changes. Exercise streaming and non-streaming paths, MCP restart recovery, status endpoints, evaluator joins, and privacy sentinels.

See [.NET and Python parity](/infra-advisor-ai/development/dotnet-python-parity/) for the current gap list.
