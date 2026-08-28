---
title: Build, Test, and Verify
description: Choose the smallest trustworthy verification loop for a change
docType: maintainer
audience:
  - contributor
maturity: stable
verifiedOn: 2026-08-27
---

Verification should answer one question: **what evidence would show that this change works without breaking its neighbors?** Start with the narrowest relevant check, then widen the loop when the change crosses a service or deployment boundary.

## Pick a verification scope

| Change | First check | Widen when |
|---|---|---|
| Python service or ingestion code | Run the owning service's tests with `uv run pytest` | A contract, shared dependency, or deployment setting changed |
| .NET service | Run the owning solution or project tests with `dotnet test` | Shared models, HTTP contracts, or telemetry changed |
| React UI | Run its test and build scripts from `services/ui` | Routing, authentication, or an API contract changed |
| Documentation | Run `npm run check` from `docs` | Navigation, components, or build configuration changed |
| Bicep | Compile the affected module, then the main template | Resource parameters or cross-module outputs changed |
| Kubernetes | Use a client-side dry run on the affected manifests | Secrets, ingress, streaming, or service discovery changed |
| End-to-end behavior | Exercise one representative request and inspect its outputs | The change affects more than one runtime boundary |

The package manifest, project file, Makefile, or CI workflow is the source of truth for exact commands. Avoid copying a complete command catalog into documentation; it becomes stale as soon as a project or target changes.

## Use the local environment deliberately

The root Compose file supplies Redis and a Redpanda-compatible Kafka broker. It does not reproduce PostgreSQL, Azure OpenAI, Azure AI Search, Blob Storage, Kubernetes networking, or the Datadog Agent.

That makes local development useful for focused service work, but not a claim of production parity. See [Local development](/infra-advisor-ai/development/local-setup/) for the supported topology and current UI proxy boundaries.

Before running a service:

1. Read its package manifest and sample environment file.
2. Supply required credentials through ignored local configuration.
3. Start only the dependencies the behavior needs.
4. Confirm the health endpoint before testing a higher-level flow.

## Verify behavior at its observable boundary

A passing unit test is necessary evidence, but it may not prove an observability feature works. Match the check to the claim:

| Claim | Evidence |
|---|---|
| An endpoint works | Response status and schema |
| A tool is selected | Response metadata plus the agent trace |
| A trace is correlated | Matching service, trace, and session attributes in Datadog |
| A metric is emitted | A recent tagged point in Metrics Explorer |
| A stream works through ingress | Incremental events from the public route, not only localhost |
| A data refresh works | Successful DAG task output, manifest, and indexed sample |
| A deployment is healthy | Rollout status, readiness, and one representative request |

For the specific signals this project emits, use [Observability](/infra-advisor-ai/observability/) and [Agent Observability monitoring](/infra-advisor-ai/llm-engineering/monitoring/spans-and-traces/).

## Deployment checks

Deployment commands change state. Inspect the current `Makefile` help before using them:

```bash
SKIP_DOTENV=1 make help
```

Important boundaries:

- `make deploy-k8s` applies the application's manifest groups but does not install the Datadog Operator configuration; use the dedicated Datadog target documented in [Kubernetes deployment](/infra-advisor-ai/deployment/kubernetes/).
- Secret targets read local environment values and create Kubernetes Secrets. Never paste secret values into a command transcript, issue, or documentation page.
- Bicep compilation and Kubernetes dry runs are validation steps; deployment targets are not.
- Airflow recovery targets may be destructive. Follow the prerequisites and warnings in the Makefile rather than treating a copied command as permission to run it.

After a rollout, check the affected workload's rollout status, recent logs, and one representative user flow. A green pod alone does not prove its upstream dependencies or telemetry path work.

## When a check fails

Localize the failure before changing code:

1. Re-run the narrowest failing check.
2. Identify whether the boundary is code, configuration, dependency, network, or telemetry export.
3. Inspect the owning service's logs or test failure—not every cluster log.
4. Add a regression test when the failure exposes a durable invariant.
5. Update learning content only when the behavior, limitation, or verification method changed.

See [Testing strategy](/infra-advisor-ai/development/testing/) for test layers and [Deployment](/infra-advisor-ai/deployment/) for environment-specific workflows.
