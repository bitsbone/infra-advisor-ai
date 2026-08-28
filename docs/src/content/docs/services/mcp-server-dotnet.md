---
title: MCP Server (.NET)
description: Understand the stateful Model Context Protocol transport, provider parity, and session-affinity requirement
docType: reference
audience:
  - application-developer
  - platform-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 4
---

The .NET MCP server implements the same eleven infrastructure-data jobs as Python with .NET provider adapters and OpenTelemetry. The .NET Agent API is its only application consumer.

## Stateful transport matters

ModelContextProtocol.AspNetCore uses session-aware Streamable HTTP at `/mcp`. The client initializes a session and expects later requests to reach compatible state. Kubernetes therefore applies `sessionAffinity: ClientIP` so one agent pod stays with one MCP pod for that session.

This is not needed on Python's stateless MCP path. If session affinity or an MCP pod restart breaks the .NET session, the Agent API's cached client can refresh and retry at a safe boundary.

## Shared behavior, separate code

The service covers bridges, disasters, energy, water, ERCOT, TxDOT, project knowledge, procurement opportunities, contract awards, web procurement, and document drafting. Exact parameter schemas come from the MCP server at runtime.

Cross-language provider tests should compare meaning, bounds, error categories, and redaction rather than serialized implementation details. The procurement tool in both languages produces the same versioned artifact schema and safe source links.

Document drafting uses embedded Scriban templates for cost estimates, funding memos, risk summaries, and scopes of work. Templates create structure from already gathered facts; the drafting tool should not be used as a retrieval substitute.

## Observability

ASP.NET Core and outbound HTTP instrumentation export through OTLP. Health-probe routes are filtered before export. Provider logs use bounded structured fields and must omit search text, raw provider bodies, contacts, keys, and signed/query-string URLs.

The service exposes `/health`, `/livez`, and `/readyz`. Readiness reports local configuration state; it does not call every external provider and therefore cannot prove provider availability.

## Verify a change

Run the .NET MCP test project in Release configuration. For shared tools, run the Python counterpart as well. Exercise session initialization through the Agent API, restart an MCP pod, and confirm the documented affinity/reconnect behavior without duplicating or partially streaming a response.

See the [MCP tool guide](../mcp-tools/) for selection patterns and [MCP tracing](/infra-advisor-ai/llm-engineering/monitoring/mcp-clients/) for cross-service investigation.
