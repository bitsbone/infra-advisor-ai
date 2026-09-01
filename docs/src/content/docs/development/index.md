---
title: Development
description: Find the right implementation boundary, local workflow, and verification path before changing InfraAdvisor
docType: guide
audience:
  - application-developer
  - maintainer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 6
---

InfraAdvisor is a multi-runtime repository. Begin by identifying the contract you are changing: client API, agent behavior, MCP tool, provider normalization, persistence, ingestion, deployment, or telemetry. Most features cross more than one directory but should still have one clear owner.

## Repository map

| Path | Responsibility |
|---|---|
| `services/agent-api` | Python agent, streaming API, memory, evaluations, media |
| `services/agent-api-dotnet` | .NET agent, streaming API, evaluators, media |
| `services/mcp-server*` | Language-matched MCP tools and provider adapters |
| `services/auth-api` | Users, authentication, reset and admin workflows |
| `services/ui` | React web application and browser RUM |
| `mobile` | Native iOS, native Android, and .NET MAUI clients |
| `services/adf-functions` | Azure Functions ingestion app, domain modules, and data contracts |
| `contracts` | Versioned cross-service payload schemas and fixtures |
| `infra/bicep` | Azure infrastructure source of truth |
| `k8s` | Runtime configuration and workload manifests |
| `datadog` | Agent, dashboard, monitor, and synthetic definitions |
| `docs` | This learning site |

## Before changing code

1. Read the nearest service README and tests.
2. Find the public or cross-service contract affected by the change.
3. Identify privacy and observability behavior alongside functional behavior.
4. Update both language implementations only when parity is part of the feature.
5. Choose the smallest verification that exercises the real boundary.

Repository-level contributor instructions in `AGENTS.md` govern planning, verification, and documentation. The public [documentation approach](/infra-advisor-ai/agent-guides/documentation/) explains how feature changes should become learning content without forcing every page into one template.

## Continue

- [Local setup](./local-setup/) runs the supported local subset and calls out missing dependencies honestly.
- [Testing](./testing/) chooses checks by change surface rather than stale test counts.
- [Conventions](./conventions/) records invariants that prevent common runtime and telemetry failures.
- [.NET and Python parity](./dotnet-python-parity/) tracks deliberate differences and remaining gaps.
