---
title: Preserve project conventions
description: Apply the small set of coding and deployment invariants that protect runtime behavior, secrets, and telemetry
docType: reference
audience:
  - application-developer
  - maintainer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 3
  label: Conventions
---

These conventions exist because violating them changes runtime behavior—not simply style. Language formatting and package versions remain in each project file and lockfile.

## Python instrumentation starts first

Deployable Python entrypoints import `ddtrace.auto` before libraries that should be patched. Moving it below FastAPI, HTTP, database, Kafka, or AI imports can silently remove automatic coverage.

Use explicit spans for application meaning that a library cannot infer. Avoid wrapping an already instrumented call merely to rename it; that produces duplicate spans.

## Secrets and sensitive data stay out of source

- Read required configuration from environment and fail clearly at startup.
- Keep optional features explicit when their configuration is absent.
- Never place credentials, JWTs, signed URLs, prompts, responses, filenames, or provider bodies in logs, metric tags, or custom span attributes.
- Pass Kubernetes secrets through `secretKeyRef` or controlled creation targets, not committed manifests.

Client tokens designed for browser/mobile SDK initialization are not API keys, but they still belong in environment or release configuration so applications can be separated cleanly.

## MCP tools return actionable failures

Provider failures should become a bounded result with a stable code/category and retry guidance when the agent can recover. Do not return raw exception prose or response bodies. Reserve thrown exceptions for programming errors or boundaries where the framework must produce an HTTP failure.

## Cross-language contracts are versioned

When Python, .NET, and clients share a payload:

- keep a canonical schema or model;
- use sanitized fixtures;
- make additive changes compatible;
- give breaking changes a new major version;
- require consumers to ignore unknown versions safely.

The chat-artifact contract is the reference implementation of this rule.

## Kubernetes identity is consistent

Workloads use the correct namespace, a stable application label, Unified Service Tagging labels, and the namespace-local GHCR pull secret. Service, environment, and version values must agree between pod labels and runtime telemetry.

Use immutable image identity for releases. A literal `latest` tag is not a useful version attribute and can prevent platforms from recognizing an update.

## Ingestion Functions stay domain-scoped

Each `services/adf-functions/domains/*.py` module owns exactly one source's `fetch_and_store` logic; shared chunking/embedding/upsert logic lives in `shared/` and is never duplicated per-domain. Provider-specific validation and field mapping stay in the domain module — `function_app.py` only wires HTTP routes to domain functions, it never contains fetch logic itself.

## Metrics stay bounded

Metric names should describe a stable aggregate question. Tags use controlled vocabularies such as tool, domain, result, or backend. User, conversation, trace, URL, query, and arbitrary error values belong elsewhere.

## Source schemas remain exact

Provider field names, including FHWA NBI identifiers, retain their source spelling at the provider boundary. Normalize only into an explicit downstream contract; do not casually rename raw fields and break mapping or fixtures.

See [Build, test, and verify](/infra-advisor-ai/agent-guides/build-test-verify/) for command selection.
