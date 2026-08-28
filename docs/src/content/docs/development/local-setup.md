---
title: Run the supported local stack
description: Start the local infrastructure and one agent path while recognizing which production dependencies are not supplied by Docker Compose
docType: guide
audience:
  - application-developer
maturity: partial
verifiedOn: 2026-08-27
sidebar:
  order: 1
  label: Local setup
---

The checked-in Docker Compose file supplies Redis, a Redpanda-compatible Kafka endpoint, and the two synthetic topics. It does **not** supply PostgreSQL, Azure OpenAI, Azure AI Search, or Blob Storage. A complete authenticated web workflow therefore needs additional local or remote dependencies.

## 1. Configure external services

```bash
cp .env.example .env
set -a
source .env
set +a
```

Use development credentials and keep `.env` ignored. At minimum, agent behavior needs the configured Azure OpenAI and Search resources. Authentication and durable conversations require a reachable PostgreSQL `DATABASE_URL`.

## 2. Start Redis and Kafka

```bash
docker compose up -d
docker compose ps
```

Wait for Redis and Kafka to become healthy. `kafka-init` creates `infra.query.events` and `infra.eval.results`.

## 3. Start the Python MCP server

```bash
cd services/mcp-server
uv sync --frozen
LOCAL=1 uv run uvicorn src.main:app --reload --port 8000
```

Verify `http://localhost:8000/health` before starting the agent.

## 4. Start one agent backend

Python:

```bash
cd services/agent-api
uv sync --frozen
MCP_SERVER_URL=http://localhost:8000/mcp \
REDIS_HOST=localhost \
KAFKA_BOOTSTRAP_SERVERS=localhost:9092 \
uv run uvicorn src.main:app --reload --port 8001
```

.NET can run separately on another port, preferably against the .NET MCP server:

```bash
cd services/mcp-server-dotnet
dotnet run --urls http://localhost:8004

# in another shell
cd services/agent-api-dotnet
MCP_SERVER_URL=http://localhost:8004/mcp \
REDIS_HOST=localhost \
KAFKA_BOOTSTRAP_SERVERS=localhost:9092 \
dotnet run --urls http://localhost:8003
```

Both paths still require the relevant Azure environment variables.

## 5. Add authentication only with PostgreSQL

The Auth API is PostgreSQL-specific; SQLite is not a supported drop-in. Start or connect to a disposable PostgreSQL database, set `DATABASE_URL` and `JWT_SECRET`, then run:

```bash
cd services/auth-api
uv sync --frozen
uv run uvicorn src.main:app --reload --port 8002
```

The service creates its schema on startup. Do not point local experiments at a shared production database.

## 6. Understand the UI limitation

The current Vite development proxy listens on port 3000 and forwards `/api` to the Python Agent API (or `VITE_AGENT_API_URL`). It does not reproduce the deployed nginx routes for `/auth` and `/api-dotnet`. Running `npm run dev` is useful for frontend work, but a complete local multi-backend/auth flow needs explicit proxy work or the deployed environment.

That limitation is documented here so a developer does not spend time debugging services that Vite never routes to.

## Optional local telemetry

Without a reachable Datadog Agent, disable tracing to reduce warnings:

```bash
DD_TRACE_ENABLED=false uv run uvicorn src.main:app --reload --port 8001
```

Disabling telemetry changes what can be verified. Re-enable it when working on tracing, correlation, security, or evaluation behavior.

Stop local infrastructure with `docker compose down`. Continue to [Testing](../testing/) before submitting changes.
