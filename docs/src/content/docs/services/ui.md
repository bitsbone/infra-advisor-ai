---
title: Web UI
description: Understand the browser client's state, streaming, backend routing, evidence presentation, and RUM privacy boundary
docType: concept
audience:
  - frontend-developer
  - product-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 7
---

The React UI is a client of shared service contracts, not an agent runtime. It owns authentication state, conversation navigation, streaming presentation, evidence cards, model/backend choice, the direct-tool sandbox, administration, and browser observability.

## Core interaction loop

1. Authenticate and hold the bearer token in browser storage.
2. Create or restore a durable conversation.
3. Lock that conversation to its saved backend/model metadata.
4. Upload current-turn attachments to the selected backend.
5. consume SSE steps, tool events, artifacts, text, and completion metadata.
6. persist/restore the conversation through the selected backend.
7. submit feedback against the response's trace and span identity.

The client accepts differences in Python and .NET conversation response envelopes through a small compatibility layer. Unknown artifact versions or malformed evidence must not interrupt answer rendering.

## Backend routing

The deployed nginx proxy maps `/api` to Python, `/api-dotnet` to .NET, and `/auth` to the Auth API. The selected backend also controls media upload, tools, suggestions, and conversation requests so one workflow does not silently cross implementations.

The current Vite development server reproduces only the Python `/api` proxy. See [Local setup](/infra-advisor-ai/development/local-setup/) before expecting full local auth or .NET routing.

## Streaming and recovery

The SSE parser handles fragmented frames and typed event variants. It records tool progress and artifacts before final text completion. Cancellation or failure must finish UI and telemetry state exactly once.

If the .NET MCP session expires before any unsafe streamed output, the backend can reconnect and restart. Once output has reached the client, the service returns a stable retry instruction instead of replaying work and duplicating content.

## Evidence and sandbox

Assistant citations and structured artifacts provide safe source links. Evidence cards render known contract versions; source URLs are sanitized before opening. Tool steps can prefill the authenticated Sandbox for inspection, but direct invocation remains external-data handling and must not expose credentials.

## Administration

The admin view manages users and displays read-only .NET evaluator and AI Guard diagnostics. Diagnostic panels intentionally query the .NET routes regardless of the currently selected chat backend because those pipelines are backend-specific.

## Browser telemetry

RUM captures views, resources, replays, errors, and bounded workflow actions. Query actions retain length and domain—not prompt content. Feedback, evidence, copy/report, and media actions use controlled metadata. Session Replay masks input.

Helpful, Not helpful, and Report submit authenticated feedback against the assistant response span through the selected backend. The action shows pending, success, and retryable failure states; it never treats a failed network request as successful. The backend sends the signal through Datadog's Evaluations API with `event_kind=feedback`, while the browser records only bounded RUM action metadata and never receives the Datadog API key.

The UI sends RUM session metadata and distributed trace headers, but client comments or headers do not themselves guarantee an Agent Observability session tag. Verify the actual backend trace fields.

## Verify a change

Run the TypeScript/build checks, then exercise narrow and wide layouts, keyboard/focus behavior, streaming fragmentation, cancellation, restored conversations, backend locking, feedback success/failure, unknown artifacts, unsafe links, authentication expiry, and RUM privacy sentinels.

See [Browser RUM](/infra-advisor-ai/observability/rum/) for the investigation workflow.
