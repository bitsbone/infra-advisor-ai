---
title: Agent API (Python)
description: Understand the LangGraph router/specialist path, public contracts, state boundaries, and Datadog SDK instrumentation
docType: reference
audience:
  - application-developer
  - observability-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 1
---

The Python Agent API is the Datadog-SDK implementation of the InfraAdvisor experience. FastAPI exposes client contracts; LangGraph and LangChain coordinate a router and domain specialists; the matching Python MCP server supplies tools.

## Request path

```text
authenticated request
  → validate conversation and attachment ownership
  → AI Guard pre-flight
  → restore tenant-scoped Redis memory
  → transcribe or describe current-turn media
  → classify domain and retrieve context
  → router chooses specialist
  → specialist reasons with its tool subset
  → extract sources and artifacts
  → persist messages and stream/return result
  → schedule optional faithfulness task
```

Specialists cover transportation, water/energy, business development, document work, and general questions. Tool partitioning narrows each agent's choices; it is a deliberate contrast with the .NET single-agent path.

## Endpoint groups

| Group | Endpoints | Purpose |
|---|---|---|
| Query | `POST /query`, `POST /query/stream` | Non-streaming and SSE agent execution |
| Media | `POST /media/upload` | Validate and upload one image/audio reference contract |
| Discovery | `GET /models`, `GET /tools` | Client model/tool options |
| Suggestions | `GET /suggestions/initial`, `POST /suggestions` | Cached opening and contextual prompts |
| Sandbox | `POST /tools/{tool_name}` | Direct authenticated MCP invocation |
| Quality | `POST /feedback` | Span-linked user feedback |
| Conversations | create/list/get/delete routes | Durable user-owned threads |
| Runtime | `/health`, `/livez`, `/readyz`, session delete | Diagnostics, probes, hot-memory cleanup |

Use the running FastAPI OpenAPI schema for exact bodies and responses. Streaming emits typed events for workflow steps, tool start/end, artifacts, text chunks, completion metadata, and safe errors.

## State and tenancy

JWT `sub` is the user identity. Conversation ownership is checked before memory restoration or model work. Redis keys derive from user plus conversation/session input; PostgreSQL queries repeat ownership predicates around durable writes. Missing and foreign conversations intentionally share a not-found response.

When `DATABASE_URL` is absent, stateless requests can still work, but durable conversation routes or conversation-bound queries degrade explicitly. Redis remains ephemeral even when PostgreSQL persistence is enabled.

## Attachments and artifacts

Media upload creates generated Blob names and read-only references. Query handlers validate those references again before downloading. Only the current turn reprocesses media.

Recognized procurement MCP results become versioned chat artifacts. They travel in normal responses or SSE, persist with assistant messages, and are rebuilt from allowlisted fields rather than raw provider objects.

## Observability

`ddtrace.auto` loads before framework imports. Automatic integrations cover supported web, data, Kafka, model, and MCP calls; explicit `LLMObs` spans express workflow, router, specialist, retrieval, media, and evaluation meaning.

The background faithfulness task annotates its own task span and emits `eval.faithfulness_score`. It does not use the .NET external-evaluator dispatcher. User feedback does use `LLMObs.submit_evaluation()` against a known span.

Custom telemetry uses bounded metadata. Provider-adapter capture, media URLs/content, prompts, responses, session IDs, and credentials follow the privacy rules documented in [Agent Observability](/infra-advisor-ai/llm-engineering/).

## Verify a change

Run focused tests under `services/agent-api/tests`, then `make test-agent`. For contract changes, exercise both `/query` and `/query/stream`, conversation ownership, cancellation, malformed attachments, artifact persistence, and the absence of sentinel sensitive values in telemetry.

Compare with [.NET Agent API](../agent-api-dotnet/) to determine whether user-visible parity is required.
