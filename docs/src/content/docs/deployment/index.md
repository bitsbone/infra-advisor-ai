---
title: Deployment
description: Understand the separate infrastructure, cluster, data-initialization, and verification gates
docType: guide
audience:
  - platform-engineer
  - maintainer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 5
---

InfraAdvisor deploys in layers. Azure Bicep creates managed resources and AKS; Kubernetes manifests and Helm install workloads; Airflow initializes derived data; verification proves each boundary independently.

## Deployment flow

```text
environment preflight
      │
      ├─ Bicep → Azure resources and AKS
      │
      ├─ cluster access + operators
      │
      ├─ namespace-scoped secrets
      │
      ├─ Kubernetes manifests + Airflow Helm release
      │
      ├─ DatadogAgent custom resource
      │
      └─ selected ingestion canaries → application acceptance
```

Do not collapse these into one “deployment succeeded” signal. A successful Bicep deployment says nothing about image pulls; ready pods say nothing about a populated search index; a reachable UI says nothing about Datadog correlation.

## Make targets by intent

| Intent | Target |
|---|---|
| Validate deployment environment | `make check-env` |
| Provision Azure | `make deploy-infra` |
| Configure kube context | `make get-credentials` |
| Create all application secrets | `make create-secrets` |
| Apply workloads and Airflow | `make deploy-k8s` |
| Apply DatadogAgent CR | `make apply-datadog-agent` |
| Wait for selected rollouts | `make rollout-status` |
| Inspect pods | `make check-pods` |
| Trigger approved ingestion canaries | `make run-dags` |

The Makefile is executable documentation. Run `make help` for the current target catalog rather than copying a long list from this page.

## Airflow safety boundary

Airflow DAGs ship inside an immutable application image. Install and upgrade targets verify the exact image contract before Helm changes the cluster. The destructive recovery target requires an explicit acknowledgement because removing the release or namespace can discard metadata and logs.

Never copy changed DAG files into a running pod or persistent volume. Build, verify, publish, and upgrade the image.

## Release identity

CI builds changed services and deploys immutable commit-tagged images. Keep the same version identity in Kubernetes labels and Datadog telemetry. Restarting a Deployment that still references an ambiguous tag is not proof that new code is running.

Continue to [Prerequisites](./prerequisites/), then follow the [Deployment quickstart](./quickstart/). Use [Kubernetes resources](./kubernetes/) as an ownership map, not a substitute for inspecting manifests.
