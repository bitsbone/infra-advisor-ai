---
title: Follow a browser session into the backend
description: Use privacy-safe RUM actions, resources, Session Replay, and distributed tracing to explain the web experience
docType: guide
audience:
  - frontend-developer
  - observability-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 2
  label: Browser RUM
---

Browser RUM explains what happened around an agent request: the view, user action, resource timing, JavaScript errors, long tasks, and replay context. Distributed tracing connects the matching same-origin API request to APM.

## Initialization boundary

`services/ui/src/lib/datadog-rum.ts` initializes RUM only when the application ID and client token are supplied at build time. The service, environment, and version identify the frontend release. Session Replay uses `mask-user-input`, and same-origin URLs are eligible for trace-header injection.

The current configuration samples all sessions and replays for the demo environment. That is a deliberate learning setting, not a production-volume recommendation.

## Custom actions retain shape, not content

The UI records stable workflow facts:

| Action family | Safe attributes |
|---|---|
| Query | query length and routed domain |
| Suggestion | a bounded preview |
| Citation or evidence | document type, score, or card count |
| Feedback | category and domain |
| Attachment | kind, size, duration, and bounded failure category |

The query text is **not** attached to `query_submitted`. This corrects an older design that would have copied prompts into RUM actions. Review new actions for the same property: retain what supports product analysis without duplicating user content, tokens, filenames, URLs, or responses.

## Two different correlations

The RUM SDK injects distributed trace headers into allowed API requests. Python and .NET continue supported trace context, producing the RUM resource-to-APM link.

The UI also sends `X-DD-RUM-Session-ID` as application metadata. Do not assume that this automatically becomes an Agent Observability `session.id`; the current agent instrumentation does not add that custom tag. Treat browser session identity, chat conversation identity, and one request's trace identity as distinct concepts.

## Verify one user journey

1. Open a new browser session and submit a tool-using query.
2. In RUM, locate the `query_submitted` action and confirm it contains length/domain metadata but not the prompt.
3. Open the API resource and follow its backend trace.
4. Confirm service, environment, and version match the deployed frontend and backend.
5. Inspect the replay and verify input masking.
6. Compare resource duration with time to first streamed content; a complete streaming request duration is not itself time to first token.

## Release diagnostics

Production builds upload JavaScript source maps with the same service and release version used by RUM. A source map is useful only when those identifiers match the deployed bundle. Verify one controlled error resolves to the original TypeScript line after each release-pipeline change.

Continue to [APM and tracing](../apm/) for backend correlation or [Mobile RUM](../mobile-rum/) for the native client comparison.
