---
title: Choose the right verification
description: Run checks by affected contract instead of relying on test counts that become stale
docType: guide
audience:
  - application-developer
  - maintainer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 2
  label: Testing
---

Test counts and durations change continuously. Select verification from the behavior you changed, and use CI as a second execution—not the first time the relevant check runs.

## Change-to-check map

| Change surface | Minimum focused check | Broader confidence |
|---|---|---|
| Python MCP tool/provider | tool test file with mocked HTTP | `make test-mcp` |
| Python agent/API | focused pytest module | `make test-agent` |
| .NET agent or MCP | matching test-project filter | full .NET Release build and tests |
| Auth or persistence | API tests; PostgreSQL integration test when SQL changes | service suite |
| UI | TypeScript check and production build | exercised browser workflow |
| MAUI/native mobile | relevant unit/view-model tests | platform Debug/Release build and device acceptance |
| Airflow DAG/helper | focused ingestion test | real DagBag plus built-image contract |
| Kubernetes/AppSec | executable manifest contract test | rendered/apply validation in a disposable environment |
| Documentation | docs build, internal links, content rules | visual review at narrow and wide widths |

## Common commands

```bash
make test-mcp
make test-agent
make test-load-gen
make test-all
make test-airflow
make test-airflow-container
```

Run .NET tests from their explicit test projects so production and test directories do not get confused. Use `dotnet test ... --filter <expression>` for a focused case, then a Release build/test before changing deployment code.

For the web client:

```bash
cd services/ui
npm ci
npm run build
```

## Mock the external boundary, not your logic

Provider tests should assert request mapping, pagination, normalization, empty results, bounded failures, and redaction. Mock the external HTTP response while executing the real tool or service logic. Fixtures must use invented identifiers and secrets.

Agent tests should exercise routing, streaming event order, memory isolation, artifact extraction, attachment validation, and failure degradation without making paid model calls. Contract tests should feed the same sanitized payloads to both language implementations where parity matters.

## Preserve the real runtime gate

Lightweight unit tests cannot prove an Airflow image contains its packages or that a DAG parses under the deployed version. The ingestion CI job runs tests under the lock, builds the image, and executes its embedded verification script.

Likewise, a mocked repository cannot prove PostgreSQL JSONB round trips. CI includes a real PostgreSQL integration case for conversation artifacts.

## Test observability as behavior

For telemetry changes, assert both presence and absence:

- the expected span, metric, action, or log exists with bounded fields;
- sentinel prompts, tokens, signed URLs, filenames, and provider bodies do not appear;
- one logical operation is not double-instrumented;
- failure and cancellation close lifecycle records exactly once;
- correlation IDs point to the intended request or span.

## Before handoff

Run the focused check, the relevant service/build suite, `git diff --check`, and repository secret hygiene. If a live Datadog or mobile acceptance step cannot run locally, state that gap explicitly rather than marking it verified from code inspection alone.
