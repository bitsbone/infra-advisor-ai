---
title: Project Map
description: Find the code and source of truth for each part of the learning lab
docType: maintainer
audience:
  - contributor
maturity: stable
verifiedOn: 2026-08-27
---

Use this map to find an owner, then read that directory's code and configuration. It intentionally avoids copying ports, image tags, resource sizes, API URLs, and dependency versions that already have a more reliable source of truth.

## Runtime surfaces

| Area | Primary location | What to inspect first |
|---|---|---|
| Python agent | `services/agent-api` | Route handlers, agent graph, instrumentation, project manifest |
| .NET agent | `services/agent-api-dotnet` | Endpoints, agent orchestration, diagnostics, project files |
| Python MCP server | `services/mcp-server` | Tool registration, schemas, data clients, instrumentation |
| .NET MCP server | `services/mcp-server-dotnet` | Tool classes, server lifetime, diagnostics, project files |
| Authentication | `services/auth-api` | Routes, persistence, JWT and bootstrap behavior |
| Web application | `services/ui` | React source, Vite development proxy, nginx production routing |
| Mobile clients | `mobile` | Platform README files, app configuration, RUM setup |
| Synthetic traffic | `services/load-generator` | Kafka producer/consumer flow and payload contracts |

The [Services](/infra-advisor-ai/services/) section explains each runtime's purpose and its important behavioral boundaries.

## Data and infrastructure

| Area | Primary location | Source of truth |
|---|---|---|
| Data ingestion | `services/adf-functions/domains`, `infra/bicep/modules/data-factory.bicep` | Function domain modules, ADF pipeline definitions, schedules, and dataset scope |
| Kubernetes | `k8s` | Namespaces, Services, Deployments, configuration, operators |
| Azure resources | `infra/bicep` | Resource definitions, parameters, and module outputs |
| Local dependencies | `docker-compose.yml` | Services and host mappings available during local development |
| Deployment workflows | `Makefile` | Current targets, prerequisites, and warnings |
| CI | `.github/workflows` | Build, test, image, and deployment automation |
| Datadog assets | `datadog` and `k8s/datadog` | Checked-in dashboards and cluster configuration |

Use [Architecture](/infra-advisor-ai/architecture/) for the system model, [Data pipeline](/infra-advisor-ai/data-pipeline/) for dataset flow, and [Deployment](/infra-advisor-ai/deployment/) before changing a live environment.

## Learning content

| Concern | Location |
|---|---|
| Public course content | `docs/src/content/docs` |
| Reusable learning components | `docs/src/components` |
| Navigation and site configuration | `docs/astro.config.mjs` |
| Content quality checks | `docs/scripts/check-content.mjs` |
| Contributor documentation policy | `AGENTS.md` and [Documentation approach](/infra-advisor-ai/agent-guides/documentation/) |

The documentation should interpret the implementation, not replace it. When a precise inventory matters, derive it from the current schemas, manifests, or project files.

## Trace a change across boundaries

Changes often have more than one owner:

- A new MCP tool can affect the tool schema, both agent clients, tests, observability, and the tool-selection lesson.
- A new public route can affect the upstream service, nginx, authentication, streaming behavior, and deployment checks.
- A telemetry change can affect runtime instrumentation, Datadog configuration, privacy behavior, dashboards, and the verification instructions.
- A dataset change can affect an ingestion Function, storage or search schemas, MCP behavior, examples, and claims about geographic or temporal scope.

Start at the user-visible behavior, follow its call path, and update only the sources whose contracts or learning outcomes actually changed.
