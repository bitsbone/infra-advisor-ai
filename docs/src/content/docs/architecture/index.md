---
title: Architecture
description: Understand the boundaries that keep user experience, agent reasoning, external data, persistence, and telemetry independently inspectable
docType: guide
audience:
  - application-developer
  - platform-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 2
---

InfraAdvisor is a learning system built from replaceable boundaries rather than one agent process. A web or mobile client talks to one of two agent backends; each agent uses a language-matched MCP server for external data. PostgreSQL, Redis, Kafka, Azure Data Factory/Functions, Azure services, and Datadog support durable state, synthetic work, ingestion, models, retrieval, and observation.

## Read the architecture by responsibility

| Boundary | Owns | Does not own |
|---|---|---|
| Client | Interaction, streaming display, evidence presentation | Provider data normalization or agent reasoning |
| Agent API | Routing, memory, model orchestration, evaluation scheduling | Direct knowledge of provider API shapes |
| MCP server | Tool contracts, provider calls, normalized results | Conversation policy or UI state |
| Auth API | Identity, credentials, token lifecycle | Agent memory |
| Data pipeline | Source snapshots and search-index refresh | Request-time agent execution |
| Observability | Evidence about behavior and quality | Application persistence or control flow |

Python and .NET implement parallel agent and MCP paths. They aim for comparable product behavior while deliberately using different framework and instrumentation approaches.

## Design principles

- Keep provider details behind MCP tool contracts.
- Keep durable conversation data in PostgreSQL and replaceable hot memory in Redis.
- Treat telemetry as evidence, not an application database.
- Propagate trace context across every service boundary.
- Degrade optional retrieval, evaluation, and observability work without hiding the failure.
- Normalize sensitive external data before it reaches clients or broad telemetry.

## Choose a view

- [System overview](./overview/) maps the deployed services and protocols.
- [Data flow](./data-flow/) follows interactive, synthetic, ingestion, and persistence paths.
- [Azure infrastructure](./infrastructure/) maps Bicep modules to their responsibilities.
- [OTel on Container Apps](./aca-otel-datadog/) compares two collector placements through a working experiment.

For implementation details, continue to [Services](/infra-advisor-ai/services/). For operational evidence, continue to [Observability](/infra-advisor-ai/observability/).
