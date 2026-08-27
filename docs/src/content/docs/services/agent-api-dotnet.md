---
title: Agent API (.NET)
description: ASP.NET Core 10 port of the Agent API with OpenTelemetry tracing
---

**Port:** 8001 | **Framework:** ASP.NET Core 10 (minimal API) | **Replicas:** 2

A full .NET 10 port of the [Python Agent API](/infra-advisor-ai/services/agent-api/). Implements the same multi-agent routing architecture, core endpoint surface, Redis session memory model, and PostgreSQL conversation persistence. Traces are emitted via **OpenTelemetry OTLP** (not ddtrace) to the Datadog Agent at port 4318.

## When to use the .NET backend

Select **.NET** in the UI backend switcher when demonstrating or benchmarking the .NET implementation. Both backends are functionally equivalent from the user's perspective — the selection persists in `localStorage` and can be changed at any time.

## Multi-agent architecture

Identical routing to the Python version:

```
POST /query
  │
  ├── Router Agent (Azure OpenAI JSON mode)
  │     Classifies domain: engineering | water_energy | business_development | document | general
  │
  └── Specialist Agent (ReAct loop, up to 10 turns)
        Receives curated tool subset for its domain
        Calls MCP Server (.NET) via JSON-RPC 2.0 over HTTP
```

## API endpoints

The .NET backend exposes the same endpoint contract as the Python version. See [Agent API (Python)](/infra-advisor-ai/services/agent-api/) for full request/response schemas. Quick reference:

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/query` | Run multi-agent pipeline; accepts an ownership-checked `X-Conversation-ID`; body may include `attachments` (see below) |
| `POST` | `/query/stream` | Stream pipeline steps, typed artifacts, answer chunks, and completion metadata as SSE |
| `POST` | `/media/upload` | Upload an allowlisted image or audio file and return a private read-SAS reference |
| `POST` | `/suggestions` | Contextual follow-up suggestions (LLM-powered) |
| `GET` | `/suggestions/initial` | Opening suggestions from Redis pool |
| `GET` | `/models` | Available Azure OpenAI deployments |
| `GET` | `/tools` | List MCP tools available to this backend |
| `POST` | `/tools/{name}` | Direct MCP tool invocation (sandbox) |
| `POST` | `/feedback` | Record user feedback |
| `GET` | `/health` | Backward-compatible connectivity diagnostics |
| `GET` | `/livez` | Shallow Kubernetes liveness |
| `GET` | `/readyz` | Cached MCP/LLM readiness |
| `DELETE` | `/session/{id}` | Clear Redis session memory |
| `POST` | `/conversations` | Create conversation record in PostgreSQL |
| `GET` | `/conversations` | List conversations for user |
| `GET` | `/conversations/{id}` | Fetch conversation with message history and optional artifacts |
| `DELETE` | `/conversations/{id}` | Delete conversation |

`POST /media/upload` is implemented independently by this service through `MediaService`, using the same MIME allowlist, 10 MB limit, private Blob Storage container, blob-naming convention, and short-lived read-SAS response contract as Python. Clients route uploads to the selected chat backend (`/api-dotnet/media/upload` for .NET), so the .NET demonstration does not depend on the Python pod. The returned reference is then sent in this service's `/query` or `/query/stream` body. See [Multimodal input](/infra-advisor-ai/llm-engineering/multimodal/) for the shared contract and observability design.

User identity always comes from the validated JWT `sub` claim; `X-User-ID` is ignored. Before either query mode restores an agent session, selects a remembered model, or invokes the agent, `ConversationService.CheckOwnershipAsync` verifies that `X-Conversation-ID` belongs to that subject. Redis model keys and serialized Microsoft Agent Framework sessions use an opaque SHA-256 tenant/session key. The append transaction repeats the owner predicate under a row lock, preventing cross-tenant writes and closing check/use races. Missing and foreign conversations are both returned as `404`.

Both query modes carry the same additive chat-artifact contract as the Python service: `/query` returns `artifacts`, `/query/stream` emits `artifact` SSE events, and assistant messages persist artifacts in PostgreSQL. See [Structured Chat Artifacts](/infra-advisor-ai/llm-engineering/chat-artifacts/).

## Conversation persistence

Requires `DATABASE_URL` set in the `agent-api-dotnet-secret` K8s Secret. When set, tables are created on startup (idempotent). When unset, conversation endpoints and query requests carrying `X-Conversation-ID` return `503`; stateless `/query` requests without a conversation ID still work normally.

```bash
make create-agent-api-dotnet-secret   # uses DATABASE_URL from .env if set
```

See [Agent API (Python) — Conversation persistence](/infra-advisor-ai/services/agent-api/#conversation-persistence) for the full schema; both services share the same PostgreSQL tables. The `backend` column distinguishes which service created each conversation (`"python"` vs `"dotnet"`).

## Observability

**Tracing:** OpenTelemetry OTLP HTTP to `datadog-agent.datadog.svc.cluster.local:4318`.

**App and API Protection:** The Admission Controller injects the pinned Datadog .NET 3.44.0 profiler for its security runtime. `DD_APPSEC_ENABLED=true` and `DD_API_SECURITY_ENABLED=true` enable protection and API discovery, while `DD_APM_TRACING_ENABLED=false` prevents duplicate profiler-generated application spans. OpenTelemetry remains the sole APM trace path. See [App & API Protection](/infra-advisor-ai/observability/app-api-protection/) for the architecture decision and gateway alternative.

| Span | Instrumented by | Key tags |
|------|----------------|----------|
| HTTP requests | `AddAspNetCoreInstrumentation` (auto) | `http.method`, `http.route` |
| Outbound HTTP (MCP, Azure OpenAI) | `AddHttpClientInstrumentation` (auto) | Sanitized HTTP destination and status; Blob SAS downloads are filtered out |
| PostgreSQL (conversations) | `AddNpgsql()` (auto) | `db.statement`, `db.system=postgresql` |
| LLM router + specialist calls | Microsoft.Extensions.AI and Microsoft Agents Framework OpenTelemetry decorators | Operation, model, tokens, duration, and status; sensitive message capture is disabled |

`TelemetryPrivacy.EnableSensitiveData` is the shared fail-closed switch for every GenAI decorator. Custom activities and logs exclude prompts, answers, transcripts, media bytes, SAS URLs, filenames, and chat/RUM session IDs; generated Blob names also exclude filenames and session IDs. Unexpected client errors use stable public text plus a safe exception type and trace ID instead of exception messages. See [Multimodal input](/infra-advisor-ai/llm-engineering/multimodal/) for the regression-tested boundary.

**Service name:** `infraadvisor-agent-api-dotnet` (set via `OTEL_SERVICE_NAME` in configmap).

**DBM:** `DD_DBM_PROPAGATION_MODE=full` is set in the configmap. Npgsql database spans appear in APM via OTel. Full SQL comment injection for DBM → APM linking is available once Npgsql adds DDM propagation support.

## Configuration

Environment variables (from ConfigMap + Secret):

| Variable | Source | Description |
|----------|--------|-------------|
| `AZURE_OPENAI_ENDPOINT` | Secret | Azure OpenAI resource URL |
| `AZURE_OPENAI_API_KEY` | Secret | Azure OpenAI API key |
| `DATABASE_URL` | Secret | PostgreSQL connection string (optional) |
| `AZURE_OPENAI_DEPLOYMENT` | ConfigMap | Default model deployment name |
| `AVAILABLE_MODELS` | ConfigMap | Comma-separated list of available models |
| `MCP_SERVER_URL` | ConfigMap | Points to `mcp-server-dotnet` (not Python MCP) |
| `REDIS_HOST` / `REDIS_PORT` | ConfigMap | Session memory |
| `KAFKA_BOOTSTRAP_SERVERS` | ConfigMap | Eval event stream |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | ConfigMap | `http://datadog-agent.datadog:4318` |
| `DD_APPSEC_ENABLED` | ConfigMap | Enables the injected Datadog security runtime |
| `DD_API_SECURITY_ENABLED` | ConfigMap | Enables API discovery and security features |
| `DD_APM_TRACING_ENABLED` | ConfigMap | `false`; prevents a duplicate Datadog APM pipeline while retaining AAP |

## Build and run locally

```bash
cd services/agent-api-dotnet
dotnet restore
dotnet build

AZURE_OPENAI_ENDPOINT=https://... \
AZURE_OPENAI_API_KEY=... \
MCP_SERVER_URL=http://localhost:8000/mcp \
REDIS_HOST=localhost \
dotnet run --urls http://localhost:8003
```

## Tests

```bash
dotnet test services/agent-api-dotnet.Tests/InfraAdvisor.AgentApi.Tests.csproj -c Release
```

The focused suite validates artifact boundaries, tool-call correlation, tenant-scoped session keys, Blob/SAS attachment validation, stable public errors, and the canonical envelope carried by non-streaming and SSE response models. A separate PostgreSQL-backed CI job exercises schema initialization, conversation ownership checks, rejected cross-tenant appends, message insertion, artifact JSONB round trip, correlation fields, retrieval, and cleanup against PostgreSQL 17. Local runs explicitly skip that integration test unless `TEST_DATABASE_URL` points to a disposable database. CI runs the unit project through the `test-dotnet` matrix and the persistence case through `test-dotnet-postgres` in `.github/workflows/ci.yml`.
