---
title: Services
description: Choose the service that owns a behavior before following implementation details
docType: guide
audience:
  - application-developer
  - maintainer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 4
---

InfraAdvisor separates interaction, identity, reasoning, data access, and synthetic work. The split keeps provider APIs out of the client and keeps conversation policy out of data tools.

## Ownership map

| Service | Owns | Main consumers |
|---|---|---|
| UI | Browser interaction, streaming presentation, RUM | Web users |
| Auth API | Users, JWTs, reset/admin credential workflows | Web and mobile clients |
| Python Agent API | LangGraph orchestration and Python telemetry experiment | UI, mobile, Kafka consumer |
| .NET Agent API | Microsoft agent orchestration and OTel/evaluator experiment | UI and mobile |
| Python MCP server | Stateless Python tool/provider adapters | Python agent |
| .NET MCP server | Stateful .NET MCP transport and matching tools | .NET agent |
| Load generator | Synthetic corpus selection and Kafka production | Python Kafka consumer |

Both agent backends expose comparable client contracts, including streaming, attachments, conversations, suggestions, direct tool sandboxing, and feedback. Their internal agent and observability architectures are intentionally different.

## Follow a boundary

- [Agent API (Python)](./agent-api/) explains router/specialist orchestration and the Datadog SDK path.
- [Agent API (.NET)](./agent-api-dotnet/) explains the single-agent, retrieval, OTel, and evaluator path.
- [MCP Server (Python)](./mcp-server/) explains stateless tool transport and provider normalization.
- [MCP Server (.NET)](./mcp-server-dotnet/) explains session affinity and the matching provider layer.
- [MCP tool guide](./mcp-tools/) teaches tool selection without duplicating every volatile parameter.
- [Auth API](./auth-api/) documents identity and credential-security boundaries.
- [UI](./ui/) documents client state, streaming, and safe RUM actions.
- [Load generator](./load-generator/) documents the synthetic Kafka loop and its limits.

Use each service's generated API or tool schema as the source of truth for exact request fields. These pages explain why the surface exists and how it behaves in the wider system.
