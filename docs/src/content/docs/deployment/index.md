---
title: Deployment
description: Understand the separate infrastructure, cluster, data-initialization, and verification gates
docType: guide
audience:
  - platform-engineer
  - maintainer
maturity: stable
verifiedOn: 2026-10-09
sidebar:
  order: 5
---

InfraAdvisor deploys in layers. Azure Bicep creates managed resources, AKS, and (opt-in) the Data Factory/Functions ingestion stack; Kubernetes manifests install workloads; ingestion pipelines initialize derived data; verification proves each boundary independently.

## Deployment flow

```text
environment preflight
      │
      ├─ Bicep → Azure resources, AKS, and (opt-in) ADF + Functions
      │
      ├─ cluster access + operators
      │
      ├─ namespace-scoped secrets
      │
      ├─ Kubernetes manifests
      │
      ├─ DatadogAgent custom resource
      │
      └─ ADF pipeline runs → application acceptance
```

Do not collapse these into one “deployment succeeded” signal. A successful Bicep deployment says nothing about image pulls; ready pods say nothing about a populated search index; a reachable UI says nothing about Datadog correlation.

## Make targets by intent

| Intent | Target |
|---|---|
| Validate deployment environment | `make check-env` |
| Provision Azure | `make deploy-infra` |
| Configure kube context | `make get-credentials` |
| Create all application secrets | `make create-secrets` |
| Apply workloads | `make deploy-k8s` (prod) or `make deploy-k8s ENV=dev` |
| Seed dev with test users and conversations | `make seed-dev` |
| Create the shared synthetic-test account | `make create-synthetic-user ENV=prod` |
| Deploy a pull request to dev | add the `deploy:dev` label (see [environments](/infra-advisor-ai/deployment/environments/)) |
| Apply DatadogAgent CR | `make apply-datadog-agent` |
| Wait for selected rollouts | `make rollout-status` |
| Inspect pods | `make check-pods` |
| Deploy ingestion Functions | `func azure functionapp publish` (see [data pipeline](/data-pipeline/)) |
| Trigger an ingestion pipeline | `az datafactory pipeline create-run` |

The Makefile is executable documentation. Run `make help` for the current target catalog rather than copying a long list from this page.

## Release identity

CI builds changed services and deploys immutable commit-tagged images, and stamps the same short SHA into `DD_VERSION` and the `tags.datadoghq.com/version` labels. Prod and dev are separate namespaces with separate `env` tags; see [environments](/infra-advisor-ai/deployment/environments/). Restarting a Deployment that still references an ambiguous tag is not proof that new code is running.

Continue to [Prerequisites](./prerequisites/), then follow the [Deployment quickstart](./quickstart/). Use [Kubernetes resources](./kubernetes/) as an ownership map, not a substitute for inspecting manifests.
