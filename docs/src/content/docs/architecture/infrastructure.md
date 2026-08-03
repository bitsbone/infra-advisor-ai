---
title: Azure Infrastructure
description: Bicep modules, Azure resource specs, storage paths, and deployment order
---

All Azure resources are defined as Infrastructure as Code using Azure Bicep in `infra/bicep/`. A single subscription-scoped deployment creates everything in the `rg-tola-infra-advisor-ai` resource group in `eastus`.

## Azure resources

### AKS Cluster (`aks.bicep`)

| Property | Value |
|----------|-------|
| Node count | 3 |
| VM size | Standard_D2s_v3 (2 vCPU, 8 GB RAM each) |
| Total cluster RAM | 24 GB |
| Kubernetes version | 1.30+ |
| Node OS | Ubuntu 22.04 LTS |
| Networking | Azure CNI |
| Identity | System-assigned managed identity |

The 24 GB total RAM supports all workloads with the LocalExecutor Airflow setup. PySpark jobs (future roadmap) would require larger nodes.

### Azure OpenAI (`azure-openai.bicep`)

| Deployment | Model | Version | SKU | Capacity | Use |
|------------|-------|---------|-----|----------|-----|
| `gpt-4.1-mini` | gpt-4.1-mini | 2025-04-14 | GlobalStandard | 250K TPM | Agent reasoning (router, suggestions, faithfulness eval) |
| `gpt-4.1` | gpt-4.1 | latest | GlobalStandard | 10K TPM | Specialist deep synthesis queries |
| `text-embedding-3-small` | text-embedding-3-small | 1 | Standard | 350K TPM | Vector embeddings for AI Search |

Deployments are chained sequentially (each `dependsOn` the previous) to avoid Azure provisioning conflicts.

**Whisper account (`eastus2`, separate from the above).** `whisper-001`'s `Standard` deployment SKU is not offered in every region — `eastus` (where the main account above lives) has no deployable SKU for it at all. Since a deployment's region is fixed to its parent Cognitive Services account, transcription for voice chat attachments runs through a second, dedicated account:

| Property | Value |
|----------|-------|
| Account name | `oai-infra-advisor-whisper-<env>` |
| Region | `eastus2` |
| Deployment | `whisper` (model `whisper`, version `001`), SKU `Standard`, capacity 3 |
| Consumed by | `agent-api`'s `media.py` (`AZURE_OPENAI_WHISPER_ENDPOINT`/`_API_KEY`) and `agent-api-dotnet`'s `AgentService` (keyed `AzureOpenAIClient` singleton, same env var names) |

See [Multimodal input](/infra-advisor-ai/llm-engineering/multimodal/) for the full cascade design (audio → transcript → text pipeline; images → vision content parts).

### Azure AI Search (`azure-ai-search.bicep`)

| Property | Value |
|----------|-------|
| SKU | Standard |
| Partitions | 1 |
| Replicas | 1 |
| Index name | `infra-advisor-knowledge` |
| Search mode | Hybrid (vector + BM25 keyword) |
| Vector dimensions | 1536 (text-embedding-3-small) |

The single index stores all domain knowledge. Documents are tagged with `domain`, `source`, and `document_type` fields to enable filtered search by knowledge area.

**Index schema:**

| Field | Type | Purpose |
|-------|------|---------|
| `id` | String (key) | Unique document ID |
| `content` | String (searchable) | Text chunk (500–512 tokens) |
| `content_vector` | Collection(Single) | 1536-dim embedding |
| `source` | String (filterable) | Origin system (FHWA_NBI, OpenFEMA, EIA, etc.) |
| `domain` | String (filterable) | Knowledge area (transportation, water, energy, environmental, business_development) |
| `document_type` | String (filterable) | Record type (asset_record, disaster_declaration, water_plan_project, etc.) |
| `state` | String (filterable) | US state (where applicable) |
| `county` | String (filterable) | County name (where applicable) |
| `metadata` | String | JSON blob of source-specific fields |

### Azure Blob Storage (`azure-storage.bicep`)

| Property | Value |
|----------|-------|
| Redundancy | Standard LRS |
| Tier | Hot |
| Access | Private (SAS / connection string) |

**Container paths:**

| Path | Contents |
|------|----------|
| `raw-data/nbi/texas/` | NBI bridge parquet files (weekly) |
| `raw-data/fema/` | FEMA declaration parquet files (daily) |
| `raw-data/eia/` | EIA energy parquet files (weekly) |
| `raw-data/twdb/` | TWDB water plan Excel files (monthly) |
| `raw-data/epa_sdwis/` | EPA SDWIS water system parquet files (monthly) |
| `raw-data/knowledge-docs/` | Synthetic knowledge base parquet (on-demand) |
| `raw-data/awards/` | USASpending contract award parquet (weekly) |
| `chat-media/{session_id}/{uuid}-{filename}` | User-uploaded chat attachments (images, audio) — `publicAccess: 'None'`, addressed to callers via a read-only SAS URL minted at upload time (`agent-api`'s `POST /media/upload`), not a public container path like the rows above |

### Redis (Kubernetes, not Azure PaaS)

Redis runs as a single-pod Kubernetes Deployment in the `infra-advisor` namespace. It is not Azure Cache for Redis — intentionally kept in-cluster to eliminate external latency on the hot session-read path.

| Property | Value |
|----------|-------|
| Image | `redis:7.4-alpine` |
| Persistence | None (in-memory only — session loss on restart is acceptable) |
| Port | 6379 (ClusterIP only) |

### PostgreSQL (Kubernetes Deployment)

Auth API user accounts are stored in a PostgreSQL 16 Deployment in the `infra-advisor` namespace. Airflow has its own separate PostgreSQL sidecar (managed by the Helm chart) in the `airflow` namespace.

| Property | Value |
|----------|-------|
| Image | `postgres:16-alpine` |
| Storage | PVC, ReadWriteOnce, 5Gi (cluster default storage class) |
| Port | 5432 (ClusterIP only) |

### ACA Agentic POC (`aca-agentic-poc.bicep`)

A separate, opt-in proof-of-concept — not part of the main InfraAdvisor product — built for a customer conversation about running a .NET agentic AI app on Azure Container Apps (ACA) with Datadog observability via OpenTelemetry. It reuses this resource group and the existing Azure OpenAI account (`azure-openai.bicep`'s `gpt-4.1-mini` deployment) rather than provisioning a new Azure OpenAI resource, and reuses the existing Log Analytics workspace (`monitoring.bicep`) for the Container Apps Environment's required `appLogsConfiguration`.

The **same** minimal .NET 9 agentic app (`services/aca-agentic-poc-dotnet/` — one `/run` endpoint, one Azure OpenAI chat call, one trivial tool) is deployed **twice**, as two separate Container Apps sharing one Container Apps Environment, to directly compare two different OTel-to-Datadog integration paths:

| Property | `aca-agentic-poc-managed` | `aca-agentic-poc-sidecar` |
|----------|---------------------------|---------------------------|
| OTel path | ACA's built-in managed OpenTelemetry agent (environment-level) | Datadog `serverless-init` sidecar container, in-revision |
| Containers per revision | 1 (`app`) | 2 (`app` + `datadog-sidecar`) |
| Export protocol | gRPC only (platform requirement) | HTTP/protobuf to `localhost:4318` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Auto-injected by the platform (not set in Bicep) | Explicitly set to `http://localhost:4318`, overriding the platform default |
| Where Datadog config lives | Environment's `openTelemetryConfiguration.destinationsConfiguration.dataDogConfiguration` | `datadog-sidecar` container's `DD_API_KEY`/`DD_SITE`/`DD_OTLP_CONFIG_*` env vars |

Both apps' Container Apps Environment secrets (Azure OpenAI key, GHCR registry password, Datadog API key) are passed via CLI `--parameters` at deploy time — **never** committed to a `.bicepparam` file (unlike every other Azure resource in this repo, this module's secrets flow through Bicep at all, since ACA has no equivalent to a K8s `secretKeyRef` sourced from an out-of-band resource). The module is gated behind `deployAcaAgenticPoc` (default `false`) in `main.bicep` so a routine `make deploy-infra` run doesn't require these secrets.

**Status**: Bicep module and application code are written and locally verified (unit build, container boot, `/health` check); the actual Azure deployment, Datadog wiring, and side-by-side span comparison are pending — this section should be updated with what each path actually captured (span completeness, setup complexity) once that verification is complete.

## Deploying infrastructure

```bash
# Prerequisites: az CLI logged in, subscription set
make deploy-infra
```

This runs:
```bash
az deployment sub create \
  --location eastus \
  --template-file infra/bicep/main.bicep \
  --parameters @infra/bicep/parameters/dev.bicepparam
```

**First-time deployment order:**

1. `make deploy-infra` — provision Azure resources
2. `make get-credentials` — fetch kubeconfig for AKS
3. `make create-secrets` — push all K8s secrets from `.env`
4. `make deploy-k8s` — apply all manifests and Helm releases

## Resource tags

All Azure resources are tagged:

| Tag | Value |
|-----|-------|
| `environment` | `dev` |
| `project` | `infra-advisor-ai` |
| `managed-by` | `bicep` |
