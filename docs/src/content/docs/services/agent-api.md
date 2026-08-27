---
title: Agent API (Python)
description: Multi-agent reasoning core with LangChain ReAct and LLM Observability
---

**Port:** 8001 | **Framework:** FastAPI + LangChain ReAct + LangGraph | **Replicas:** 2

The Agent API is the reasoning core of InfraAdvisor AI. A parallel .NET implementation is documented at [Agent API (.NET)](/infra-advisor-ai/services/agent-api-dotnet/). It receives natural-language queries, routes them to the appropriate specialist agent, executes MCP tool calls, synthesizes answers, and maintains session memory in Redis.

Every query produces a rich Datadog LLM Observability trace with a multi-level span hierarchy: workflow → router → specialist → tool calls → faithfulness eval.

## Multi-agent architecture

Queries flow through two sequential agents before the final answer is assembled:

```
POST /query
  │
  ├── Router Agent (gpt-4.1-mini)
  │     Classifies domain: engineering | water_energy | business_development | document | general
  │     Cost: ~200 prompt tokens + 50 completion tokens
  │
  └── Specialist Agent (LangGraph ReAct executor)
        Receives curated tool subset for its domain
        Runs ReAct loop until answer is complete
        Tools vary by specialist (see below)
```

### Specialist agents and their tool subsets

| Specialist | Domain Keywords | Tools |
|------------|----------------|-------|
| `engineering` | bridge, structural, transportation, highway, road, TxDOT, construction, civil | `get_bridge_condition`, `get_disaster_history`, `get_energy_infrastructure`, `get_water_infrastructure`, `get_ercot_energy_storage`, `search_txdot_open_data`, `search_project_knowledge`, `draft_document` |
| `water_energy` | water, energy, utility, power, grid, reservoir, drought, ERCOT, EIA, EPA | `get_water_infrastructure`, `get_energy_infrastructure`, `get_ercot_energy_storage`, `search_project_knowledge`, `draft_document` |
| `business_development` | RFP, contract, award, procurement, grant, bid, opportunity, SAM.gov, funding | `get_procurement_opportunities`, `get_contract_awards`, `search_web_procurement`, `search_project_knowledge`, `draft_document` |
| `document` | draft, template, SOW, statement of work, risk, cost estimate, funding memo | `draft_document`, `search_project_knowledge` |
| `general` | (fallback) | All 11 tools |

## API endpoints

### `POST /query`

Run the multi-agent pipeline on a user query.

**Request:**
```json
{
  "query": "What bridges in Harris County have a sufficiency rating below 50?",
  "session_id": "550e8400-e29b-41d4-a716-446655440000",
  "model": "gpt-4.1-mini",
  "attachments": [
    {"url": "https://.../chat-media/...?sig=...", "kind": "image", "mime_type": "image/jpeg", "size_bytes": 84213}
  ]
}
```

`attachments` is optional — an array of `{url, kind, mime_type, size_bytes}` references returned by this backend's `POST /media/upload` (never raw file bytes). The .NET backend exposes the same upload contract at `/api-dotnet/media/upload`, and clients route uploads to the backend selected for the chat. An `image` attachment becomes a vision content part on the LLM call; an `audio` attachment is transcribed via Whisper first and the transcript is folded into the effective query. See [Multimodal input](/infra-advisor-ai/llm-engineering/multimodal/) for the full design.

**Headers:**
- `Authorization: Bearer <jwt>` — Required
- `X-Session-ID: <uuid>` — Session for Redis memory lookup
- `X-Conversation-ID: <uuid>` — Optional continuation identifier; the API verifies ownership against the JWT subject before restoring state or invoking the agent
- `X-DD-RUM-Session-ID: <rum_session>` — Optional; links LLM Obs traces to RUM session replay

`X-User-ID` is not an authorization input. User identity always comes from the validated JWT `sub` claim. A conversation owned by another subject is returned as `404` without invoking the agent, while an unavailable conversation store returns `503` for continuation requests.

**Response:**
```json
{
  "answer": "I found 18 bridges in Harris County...",
  "sources": [{"tool": "get_bridge_condition", "snippet": "Structure 4803..."}],
  "trace_id": "3421959702764693",
  "span_id": "8721043291846321",
  "session_id": "550e8400-...",
  "model": "gpt-4.1-mini",
  "artifacts": []
}
```

`artifacts` is additive and contains bounded, versioned MCP results that a client can render as typed evidence. The procurement tool also emits the same object as an `artifact` event during `POST /query/stream`; clients must ignore unknown kinds or schema versions. See [Structured Chat Artifacts](/infra-advisor-ai/llm-engineering/chat-artifacts/).

---

### `POST /media/upload`

Upload a chat attachment (image or audio) to Blob Storage and return a read-SAS URL reference. This Python endpoint and the .NET service's matching endpoint use the same validation and storage contract, but clients call the endpoint belonging to the selected chat backend: `/api/media/upload` for Python or `/api-dotnet/media/upload` for .NET. See [Multimodal input](/infra-advisor-ai/llm-engineering/multimodal/) for the design.

**Request:** `multipart/form-data` with a single `file` field. Allowlisted content-types: `image/jpeg`, `image/png`, `image/webp`, `audio/webm`, `audio/wav`, `audio/mpeg`, `audio/ogg`. Max size 10 MB.

**Headers:**
- `Authorization: Bearer <jwt>` — Required
- `X-Session-ID: <uuid>` — Associates the upload with the active client workflow; it is not used in the Blob object name or telemetry

**Response:**
```json
{
  "url": "https://stinfraadvdev.blob.core.windows.net/chat-media/image/<generated-id>?sig=...",
  "kind": "image",
  "mime_type": "image/jpeg",
  "size_bytes": 84213
}
```

**Errors:** `415` unsupported content-type, `413` file too large, `401` missing/invalid auth, `429` rate limit (`10/minute`, tighter than `/query`'s `20/minute`).

---

### `GET /suggestions/initial`

Returns 4 infrastructure-focused opening suggestions from the Redis pool. No LLM call — responds in ~1ms from a pre-generated pool of up to 80 suggestions.

If the pool drops below 20 items, a background `_fill_pool()` task runs asynchronously to replenish it.

**Response:**
```json
{
  "suggestions": [
    "Which Texas bridges are rated structurally deficient and carry more than 5,000 vehicles daily?",
    "Compare federal infrastructure grant funding available for water utilities in drought-prone states.",
    "What construction contracts over $1M were awarded in Arizona for highway projects last year?",
    "Draft a risk summary for a bridge rehabilitation project in a flood-prone county."
  ]
}
```

---

### `POST /suggestions`

Generate follow-up suggestions based on conversation context. LLM-powered (gpt-4.1-mini).

**Request:**
```json
{
  "query": "Tell me about Texas bridges",
  "answer": "I found 18 bridges...",
  "domain": "engineering",
  "session_id": "..."
}
```

**Response:**
```json
{
  "suggestions": ["...", "...", "...", "..."]
}
```

---

### `GET /models`

List available Azure OpenAI deployment names.

**Response:**
```json
{
  "models": ["gpt-4.1-mini", "gpt-4.1"],
  "default": "gpt-4.1-mini"
}
```

---

### `POST /feedback`

Record user feedback for a specific LLM Observability trace.

**Request:**
```json
{
  "trace_id": "3421959702764693",
  "span_id": "8721043291846321",
  "rating": "positive",
  "session_id": "..."
}
```

Valid ratings: `positive`, `negative`, `reported`

**Response:** 204 No Content

The feedback is submitted via `LLMObs.submit_evaluation()` and appears under the **Evaluations** tab on the LLM Obs trace in Datadog.

---

### `GET /health`

Returns backward-compatible service diagnostics and cached MCP/LLM connectivity. Kubernetes uses shallow `GET /livez` for liveness and cached `GET /readyz` for traffic readiness; neither endpoint calls an external provider.

---

### `POST /tools/{tool_name}`

Directly invoke an MCP tool (debug/sandbox endpoint). Used by the UI Sandbox tab.

---

### `POST /conversations`

Create a new conversation record. Returns a conversation object with a server-generated UUID that the client should send as `X-Conversation-ID` on subsequent `/query` calls.

**Headers:** `Authorization: Bearer <jwt>` (required). The persisted `user_id` comes from the validated JWT `sub` claim.

**Request body (all optional):**
```json
{ "title": "Bridge analysis session", "model": "gpt-4.1-mini", "backend": "python" }
```

**Response:**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "user_id": "alice@example.com",
  "title": "Bridge analysis session",
  "model": "gpt-4.1-mini",
  "backend": "python",
  "created_at": "2026-05-05T10:00:00Z",
  "updated_at": "2026-05-05T10:00:00Z",
  "message_count": 0
}
```

Returns `503` when `DATABASE_URL` is not configured (persistence disabled).

---

### `GET /conversations`

List all conversations for a user, sorted by `updated_at` descending.

**Headers:** `Authorization: Bearer <jwt>` (required)

**Response:** Array of conversation summary objects (same shape as above, plus `message_count`).

---

### `GET /conversations/{id}`

Fetch a single conversation with its full message history.

**Headers:** `Authorization: Bearer <jwt>` (required)

**Response:** Conversation summary plus a `messages` array:
```json
{
  "id": "...",
  "messages": [
    {
      "id": "...",
      "role": "user",
      "content": "What bridges in Harris County...",
      "sources": [],
      "created_at": "2026-05-05T10:00:05Z"
    },
    {
      "id": "...",
      "role": "assistant",
      "content": "I found 18 bridges...",
      "sources": ["get_bridge_condition"],
      "trace_id": "3421959702764693",
      "span_id": "8721043291846321",
      "created_at": "2026-05-05T10:00:08Z"
    }
  ]
}
```

---

### `DELETE /conversations/{id}`

Delete a conversation and all its messages. Returns `204 No Content` on success, `404` if not found or not owned by the requesting user.

**Headers:** `Authorization: Bearer <jwt>` (required)

---

## Session memory

Session history is stored in Redis with a 24-hour TTL:

```
Key: infra-advisor:session:{sha256(jwt_sub + NUL + session_or_conversation_id)}:memory
Value: JSON list of {"type": "human"|"ai", "content": "..."} exchange objects
TTL: 86400 seconds (refreshed on write)
```

The model preference is persisted separately:
```
Key: infra-advisor:session:{sha256(jwt_sub + NUL + session_or_conversation_id)}:model
Value: "gpt-4.1-mini" | "gpt-4.1"
TTL: 86400 seconds
```

The opaque hash binds state to both the authenticated user and the client routing identifier, so two users choosing the same session or conversation ID cannot share history or model state. On `DELETE /session/{session_id}`, only the caller's tenant-scoped keys are removed.

## Conversation persistence

When `DATABASE_URL` is set, the Agent API stores every user/assistant exchange in PostgreSQL. This enables the conversation history sidebar in the UI.

**Schema:**

```sql
conversations (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     TEXT NOT NULL,
    title       TEXT NOT NULL DEFAULT 'New Conversation',
    model       TEXT,
    backend     TEXT DEFAULT 'python',   -- 'python' | 'dotnet'
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
)

messages (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    conversation_id UUID REFERENCES conversations(id) ON DELETE CASCADE,
    role            TEXT NOT NULL,       -- 'user' | 'assistant'
    content         TEXT NOT NULL,
    sources         JSONB NOT NULL DEFAULT '[]',
    steps           JSONB NOT NULL DEFAULT '[]',
    attachments     JSONB NOT NULL DEFAULT '[]',
    artifacts       JSONB NOT NULL DEFAULT '[]',
    trace_id        TEXT,               -- ddtrace trace ID for APM linking
    span_id         TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
)
```

Tables are created on startup via `init_db()` (idempotent `CREATE TABLE IF NOT EXISTS`). If `DATABASE_URL` is unset the service starts normally — all conversation endpoints return `[]` or `503`.

**Enabling persistence:**

```bash
# Set DATABASE_URL in your .env before running make create-agent-api-secret
DATABASE_URL=postgresql://user:pass@postgres.infra-advisor.svc.cluster.local:5432/infraadvisor
make create-agent-api-secret
```

**How the UI wires it in:** The UI creates a conversation on the first message of a new session, then sends `X-Conversation-ID` with the bearer token on every subsequent `/query` call. The service checks `conversations.id + conversations.user_id` before agent invocation and repeats the ownership check under a row lock while appending each exchange.

**DD_DBM_PROPAGATION_MODE:** Set to `full` in `k8s/agent-api/configmap.yaml`. Every PostgreSQL query issued by this service includes a SQL comment with the full ddtrace trace context, enabling **"View Trace"** links from Datadog Database Monitoring query samples back to the originating APM trace.

## Error responses

On unhandled 500 errors, the API returns:
```json
{
  "detail": "The service encountered an unexpected error.",
  "error_type": "RuntimeError",
  "trace_id": "3421959702764693"
}
```

The public detail is stable and never includes exception messages, provider payloads, database URLs, or stack traces. `error_type` is a bounded diagnostic category and `trace_id` is the ddtrace trace ID for the current request. The UI renders a "View trace →" link that opens the Datadog APM trace directly.

## Observability

The service enables Datadog App and API Protection and API Security in its Kubernetes ConfigMap while retaining its existing `ddtrace` APM pipeline. The pod opts in to Admission Controller configuration injection, and the image itself pins and initializes `ddtrace`. See [App & API Protection](/infra-advisor-ai/observability/app-api-protection/) for the gateway alternative, privacy boundary, and rollout checks.

**LLM Observability span tree** (per query):

```
workflow: query-processing
  task: load-history              (tags: history.turns)
  agent: router                   (tags: query.domain)
    chat_model (auto-instrumented)
  agent: infra-advisor            (tags: tools_called.count, sources.count)
    tool: get_bridge_condition    (auto-instrumented via langchain-mcp-adapters)
      http (auto-instrumented, outbound to mcp-server)
    chat_model (auto-instrumented, ReAct reasoning)
  task: extract-sources           (tags: sources.count)

(async, separate trace)
task: faithfulness-eval           (tags: query.domain, eval.faithfulness_score)
  llm (auto-instrumented OpenAI call)
```

**Privacy boundary:** raw prompts, answers, transcripts, media bytes, SAS URLs, filenames, and chat/RUM session IDs are not copied into custom LLMObs annotations, span tags, or logs. Workflow correlation uses trace/span IDs, while safe tags retain domain, model, tool, count, size, duration, and status dimensions. See [Multimodal input](/infra-advisor-ai/llm-engineering/multimodal/) for the provider-boundary pattern.

## Kafka integration

**Producer (eval results):** After each query, the agent publishes to `infra.eval.results`:
```json
{
  "query_id": "...",
  "session_id": "...",
  "answer": "...",
  "tools_called": ["get_bridge_condition"],
  "faithfulness_score": 0.92,
  "latency_ms": 3241,
  "model": "gpt-4.1-mini"
}
```

**Consumer (load generator):** A background thread consumes `infra.query.events` published by the Load Generator CronJob. Each message runs through the full `run_agent()` pipeline, producing real LLM Obs traces for synthetic queries.
