---
title: Agent Observability glossary
description: A concise reference for the terms used throughout the InfraAdvisor learning lab
docType: reference
audience:
  - application-developer
  - observability-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 10
  label: Glossary
---

Use this page as a lookup table. Definitions describe how a term affects implementation or investigation in this project.

## Application and service identity

**ML application (`ml_app`)**

The Agent Observability grouping for related spans and evaluations. InfraAdvisor uses `infra-advisor-ai` for Python and `infra-advisor-agent-api-dotnet` for .NET. A service name identifies a runtime service; an ML application identifies the AI experience shown in Agent Observability.

**Environment and version**

Deployment attributes used to separate telemetry and compare releases. A useful trace has service, environment, and version identity before project-specific tags are considered.

## Trace structure

**Trace**

A set of operations sharing a trace ID. In InfraAdvisor, one request can cross the UI-facing API, an agent backend, MCP services, external providers, and PostgreSQL.

**Span**

One timed operation within a trace. Its span ID and parent identify where it belongs in the tree.

**Span kind**

The Agent Observability role of an operation:

| Kind | Role in the trace |
|---|---|
| `workflow` | Coordinates a complete logical flow |
| `agent` | Makes a routing, planning, or delegated decision |
| `llm` | Calls a language model |
| `tool` | Invokes a tool or function |
| `task` | Performs a named non-agent step |
| `embedding` | Creates a vector embedding |
| `retrieval` | Finds contextual data |

**Context propagation**

The mechanism that carries trace identity across process and protocol boundaries. InfraAdvisor uses W3C trace context for distributed tracing. Evaluation submission separately preserves the target trace and span IDs so a score can join to an existing span.

## Instrumentation

**Automatic instrumentation**

Library-aware instrumentation that creates spans around supported framework calls. It reduces code but can describe only behavior the integration understands.

**Explicit instrumentation**

Application-created `LLMObs` spans or OpenTelemetry activities. InfraAdvisor uses it to represent routing, retrieval, privacy-safe media steps, and other orchestration with product meaning.

**OpenTelemetry (OTel)**

Vendor-neutral APIs, conventions, and protocols used by the .NET backend. Its activities and metrics travel through OTLP and the Datadog Agent.

**Datadog SDK path**

The Python backend's `ddtrace` and `LLMObs` integrations. The SDK models Datadog concepts directly and combines automatic library coverage with explicit orchestration spans.

## Conversation identity

**Client session ID**

An application identifier returned by the query API and used as a routing or memory hint. It is not automatically the same as an Agent Observability session.

**Conversation ID**

The durable chat-thread identity. InfraAdvisor derives a tenant-scoped agent-memory key from the authenticated user and conversation or session input so client-provided IDs cannot cross tenant boundaries.

**RUM session ID**

Browser Real User Monitoring context propagated into the request trace. It supports RUM-to-APM investigation; it should not be copied into custom LLM span tags merely to imitate session grouping.

## Evaluations

**Managed evaluation**

A Datadog-provided check configured in the product. It runs after eligible telemetry is ingested.

**Custom LLM-as-a-judge evaluation**

A natural-language rubric managed in Datadog. Use it when the captured trace contains the necessary evidence and the criterion benefits from rapid prompt iteration.

**External evaluation**

A result calculated outside Datadog and submitted against an existing span. InfraAdvisor's .NET deterministic and M.E.AI judges use this path.

**Relevance**

Whether the response addresses the request.

**Groundedness or faithfulness**

Whether claims are supported by supplied evidence. Implementations and scoring scales differ, so compare evaluator definitions before comparing values.

**Annotation queue**

A structured human-review workflow for traces or other supported interactions. The labels can reveal failure modes, calibrate judges, and seed regression data.

**Dataset and experiment run**

A dataset is a versioned collection of test examples. An experiment run applies one application configuration to those examples so its output and evaluations can be compared with another run.

## Project-specific terms

**Prompt version**

An identifier used to group behavior by prompt content or release. The .NET backend currently attaches a content-derived `prompt.version`; Python parity and consistent evaluator tagging remain unfinished. The full template is not exported by this implementation.

**Chat artifact**

A bounded, versioned, presentation-safe object extracted from an MCP result and carried beside assistant prose. Artifacts give clients a stable evidence contract without exposing raw provider responses.

**Join**

The association between an evaluation and the telemetry it scores. InfraAdvisor's .NET client joins external evaluations to a specific trace ID and span ID. Incorrect or lost IDs produce an unattached result even when scoring logic succeeded.

Return to the [Agent Observability Lab](./) or begin with the [Quickstart](./quickstart/).
