---
title: MCP Server (Python)
description: Understand the stateless FastMCP boundary between agent reasoning and external infrastructure providers
docType: reference
audience:
  - application-developer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 3
---

The Python MCP server turns provider-specific APIs and the project knowledge index into bounded tools. It owns authentication to sources, request mapping, normalization, and safe failure results. It does not own conversation memory or decide which tool the agent should call.

## Transport and lifecycle

FastMCP exposes Streamable HTTP at `/mcp`. The server runs in stateless HTTP mode because the Python agent adapter creates short-lived client interactions. This differs from the .NET transport and removes the need for load-balancer session affinity on this path.

`/health` reports the service and tool catalog. `/livez` and `/readyz` support shallow Kubernetes probes without calling providers.

## Provider boundaries

The tool catalog covers bridges, disasters, energy, water, ERCOT, TxDOT, federal opportunities, contract awards, web procurement, project knowledge, and document drafting. See the [MCP tool guide](../mcp-tools/) for choosing among them.

Each tool should:

1. validate and bound its input;
2. call the narrowest provider endpoint;
3. normalize source fields without inventing meaning;
4. return a stable empty, partial, or error result;
5. retain enough public provenance for citations;
6. exclude credentials, raw bodies, contacts, and arbitrary provider fields from telemetry.

`get_procurement_opportunities` is the strongest example. It combines SAM.gov and Grants.gov into the versioned `procurement_opportunities` artifact while preserving partial-provider failure and missing-field information.

## Observability boundary

`ddtrace.auto` loads first, covering supported FastAPI/HTTP/Azure/OpenAI calls. Tool wrappers add bounded counts, latency, source, and status fields.

This deployment explicitly disables Agent Observability payload capture and HTTP query-string tagging. Those controls prevent tool arguments/results and the SAM.gov key from entering broad telemetry. Ordinary APM timing, safe metrics, and normalized result-summary logs remain available.

## Failure behavior

Expected provider failures return structured data with a stable category and retry guidance so the agent can decide whether to continue. Logs record provider, status/code, response size or fingerprint, and retry class—not exception prose or the response body.

## Verify a tool change

Run the focused provider test and `make test-mcp`. Cover successful, empty, malformed, paginated, partial, timeout, authentication, and rate-limit responses as applicable. Assert the outbound request and normalized result, then search logs/spans for sentinel secrets and provider body content that must remain absent.

Compare the [.NET MCP server](../mcp-server-dotnet/) when changing a shared tool contract.
