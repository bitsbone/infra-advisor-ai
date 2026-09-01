---
title: Deploy and verify InfraAdvisor
description: Move from a validated environment to a running AKS application through explicit checkpoints
docType: guide
audience:
  - platform-engineer
  - maintainer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 2
  label: Quickstart
---

This sequence mutates Azure, Kubernetes, and Datadog resources. Complete the [prerequisites](../prerequisites/) and confirm the intended subscription and cluster before running it.

## 1. Validate configuration

```bash
set -a
source .env
set +a
make check-env
az account show
```

Stop if the subscription, tenant, or region is not the intended environment.

## 2. Provision Azure and connect to AKS

```bash
make deploy-infra
make get-credentials
kubectl config current-context
kubectl get nodes
```

Inspect the Bicep deployment result separately from node readiness.

## 3. Prepare operators and Datadog

Install the Datadog Operator according to the repository/environment process, then apply the checked-in custom resource:

```bash
make apply-datadog-agent
```

`make deploy-k8s` installs Strimzi resources for Kafka but deliberately skips the Datadog directory because the Agent is operator-managed.

## 4. Deploy workloads

```bash
make deploy-k8s
make rollout-status
make check-pods
```

The deployment target creates namespace-local registry and application secrets, applies data services, then applies both Python and .NET application paths.

Do not accept `Running` alone. Check readiness, restart counts, image tags/digests, and recent events:

```bash
kubectl get pods -A
kubectl get events -A --sort-by=.lastTimestamp
```

## 5. Initialize derived data

Deploy the ingestion Function App and trigger each Azure Data Factory pipeline once (they run stopped by default after infra deploy):

```bash
cd services/adf-functions && func azure functionapp publish func-adf-infra-advisor-<env>
az datafactory pipeline create-run --factory-name adf-infra-advisor-<env> --resource-group <rg> --name pl-fema-refresh
# repeat for pl-nbi-refresh, pl-eia-refresh, pl-samgov-awards-refresh, pl-census-market-intelligence-refresh, pl-public-docs-ingestion
```

Confirm pipeline-run success (`az datafactory pipeline-run query-by-factory` or Datadog's ADF Data Jobs Monitoring) and search-index output before testing retrieval-dependent questions. See the [data pipeline overview](/data-pipeline/) for what each pipeline does and which two source families were retired rather than migrated.

## 6. Verify the product loop

1. Resolve the UI LoadBalancer address and configured DNS/TLS endpoint.
2. Register or use an authorized test account.
3. Send the same tool-using question to Python and .NET.
4. Confirm streaming, citations or artifacts, and conversation restoration.
5. Locate browser RUM, backend APM, Agent Observability, MCP/provider work, and correlated logs.
6. Confirm dashboards distinguish backend-specific metric and evaluation coverage.

## Upgrade safely

CI deploys changed services with immutable commit tags after merges to `main`. The ADF Function App is not yet wired into that CI path — redeploy it manually after a code change:

```bash
cd services/adf-functions && func azure functionapp publish func-adf-infra-advisor-<env>
```

Continue to [Kubernetes resources](../kubernetes/) for ownership and common inspection commands.
