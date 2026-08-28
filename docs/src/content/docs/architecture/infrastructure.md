---
title: Understand the Azure infrastructure
description: Map each provisioned resource to the application behavior it supports and locate its Bicep source of truth
docType: reference
audience:
  - platform-engineer
  - application-developer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  label: Azure infrastructure
---

Azure resources are defined in `infra/bicep` and deployed from the subscription-scoped `main.bicep`. This page explains responsibilities and important boundaries; the Bicep modules remain authoritative for regions, SKUs, capacities, and model versions that change over time.

## Resource map

| Resource | Responsibility | Source |
|---|---|---|
| AKS | Runs application, data, Kafka, Airflow, and Datadog workloads | `modules/aks.bicep` |
| Azure OpenAI | Chat, embedding, evaluation, and transcription models | `modules/azure-openai.bicep` |
| Azure AI Search | Hybrid retrieval over the project knowledge index | `modules/azure-ai-search.bicep` |
| Blob Storage | Raw/processed pipeline data, knowledge documents, and private chat media | `modules/azure-storage.bicep` |
| Log Analytics | Azure platform logging and ACA environment dependency | `modules/monitoring.bicep` |
| ACA proof of concept | Optional comparison of two OTLP-to-Datadog paths | `modules/aca-agentic-poc.bicep` |

## AKS application boundary

The development cluster uses three fixed `Standard_D2s_v3` system nodes, Azure CNI, Azure RBAC, OIDC, and Workload Identity. Kubernetes version and node settings are pinned in Bicep rather than repeated as an operational promise here.

Redis and the application PostgreSQL database run in the cluster. Redis is intentionally ephemeral because it holds replaceable session memory. PostgreSQL uses a persistent volume for users, conversations, and messages. Airflow has a separate metadata database managed with its deployment.

This topology is appropriate for a learning environment, not a general production recommendation. Availability, backups, autoscaling, and managed data services require a separate design decision.

## Model deployments

The main Azure OpenAI account hosts the currently selected chat and embedding deployments. A second account hosts Whisper in a region where its deployment SKU is available. The split exists because a model deployment cannot use a different region from its parent account.

Application configuration selects deployments by name. When adding or replacing a model, verify all three layers:

1. Bicep provisions the deployment in a supported region and SKU.
2. Kubernetes secrets or configuration expose the expected endpoint and deployment name.
3. traces identify the model/version actually used by the request.

## Search index

Azure AI Search provides hybrid vector and keyword retrieval. The `infra-advisor-knowledge` index stores chunk content, a 1,536-dimension embedding, source, domain, document type, state, county, and source-specific metadata.

The index is a derived serving layer. Airflow or initialization jobs rebuild it from governed source data; application services should not treat it as the system of record.

## Storage boundaries

All containers are private:

| Container | Purpose |
|---|---|
| `raw-data` | Pipeline source snapshots and normalized raw outputs |
| `processed-data` | Chunked or embedding-ready pipeline output |
| `knowledge-docs` | Documents used to build the search index |
| `chat-media` | Current-turn image and audio uploads |

Chat media uses generated `<kind>/<UUID>` blob names and expiring, blob-scoped read-only SAS URLs. It does not use filenames or session IDs in object paths. This keeps storage identity independent of user-provided metadata and supports the validation boundary described in [Multimodal input](/infra-advisor-ai/llm-engineering/multimodal/).

## Optional ACA experiment

The ACA module is disabled by default. When enabled, it deploys one minimal .NET application twice: once through ACA's managed OpenTelemetry collector and once with a Datadog sidecar. Secrets enter the deployment as parameters and are not committed to a parameter file. See [Compare OTel export paths](../aca-otel-datadog/) for the findings.

## Deployment order

```bash
make deploy-infra
make get-credentials
make create-secrets
make deploy-k8s
```

The first command provisions Azure resources. The remaining commands connect to AKS, create out-of-band Kubernetes secrets from the local environment, and apply workloads. Review the [deployment quickstart](/infra-advisor-ai/deployment/quickstart/) before running them.

All provisioned resources carry environment and project tags; module-specific tags identify management or experiment purpose. Inspect the deployed state and Bicep diff before treating this reference as proof of what is live.
