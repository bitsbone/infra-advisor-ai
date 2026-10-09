---
title: Prod and dev environments
description: Tell prod and dev apart in Datadog, and know what each environment runs, shares, and isolates
docType: reference
audience:
  - platform-engineer
  - maintainer
maturity: experimental
verifiedOn: 2026-10-09
sidebar:
  order: 4
  label: Environments
---

InfraAdvisor runs two environments on the same AKS cluster. Each is a Kubernetes namespace with its own Datadog `env` tag, so every trace, log, metric, RUM session, and LLM Observability span can be filtered to one environment.

| | Prod | Dev |
|---|---|---|
| Namespace | `infra-advisor` | `infra-advisor-dev` (planned) |
| Datadog `env` | `prod` | `dev` |
| Kustomize overlay | `k8s/overlays/prod` | `k8s/overlays/dev` (planned) |
| Deployed by | merge to `main` | `deploy:dev` PR label (planned) |

## Where the env tag comes from

- **Kubernetes workloads.** Each service's ConfigMap sets `DD_ENV`, and its Deployment carries the `tags.datadoghq.com/env` label. The Datadog Agent sets no cluster-wide `env` tag, because both namespaces share the cluster.
- **.NET OpenTelemetry services.** `TelemetrySetup.cs` copies `DD_ENV` and `DD_VERSION` onto the OTel resource as `deployment.environment` and `service.version`.
- **Version.** CI stamps each deploy's short commit SHA into `DD_VERSION` and the `tags.datadoghq.com/version` labels (`.github/scripts/pin-deployment.sh`), so `version:<sha>` identifies exactly what ran.
- **PostgreSQL DBM.** The check annotation on the Postgres Deployment carries its own `env:` tag.
- **ADF ingestion Functions.** They feed the prod search index, so they report `env:prod` through the `ddEnv` Bicep parameter, even though the Azure deployment's `environment` parameter (used in resource names) is `dev`.

The overlay contract tests in `k8s/tests/` fail if any env value in an overlay doesn't match the overlay's name.

## The 2026-10-09 cutover

Before 2026-10-09, prod reported `env:dev`. History before that date stays under `env:dev`, so when you compare across the cutover, query `env:dev` for older data and `env:prod` for newer data.

Datadog configuration that is scoped by environment needs a matching `prod` entry:

- **Feature Flags.** Prompt-version targeting is evaluated per environment. See [prompt targeting](/infra-advisor-ai/llm-engineering/monitoring/prompt-targeting/).
- **Dashboards.** Template variables that default to `env:dev` should default to `env:prod`.
- **Sensitive Data Scanner and retention filters.** Check any scanning group or filter that names `env:dev`.

## Verify

```bash
kubectl get deploy -n infra-advisor -L tags.datadoghq.com/env,tags.datadoghq.com/version
```

Every row should show `prod` and a commit SHA. In Datadog, `env:prod version:<sha>` in APM should match that SHA.
