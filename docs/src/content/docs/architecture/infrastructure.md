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

**Status**: Deployed live to `eastus2` and verified against Datadog. Findings from the side-by-side comparison:

- **Sidecar path — fully working.** `curl <sidecar-app-url>/run` produces a complete span tree in Datadog: `http.server.request (POST /run) → invoke_agent → chat → execute_tool ×2 → chat`, with correct `gen_ai.*` attributes (tool calls, tool results, model, token usage) and ACA-specific tags (`aca.app.name`, `aca.app.revision`, `aca.replica.name`). One real bug had to be fixed to get here: the app set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318` but never set `OTEL_EXPORTER_OTLP_PROTOCOL` — the .NET OTLP exporter defaults to `grpc` per the OTel spec when that var is absent, so it silently tried to gRPC-handshake against the sidecar's HTTP/protobuf port and dropped every span with no error output anywhere (console, `az containerapp logs show`, or `az containerapp exec`). Setting `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` explicitly on the sidecar app fixed it immediately.
- **Managed-agent path — does not work, despite following documentation exactly.** Two problems surfaced:
  1. The platform does **not** auto-inject the standard `OTEL_EXPORTER_OTLP_ENDPOINT` env var into the app container, contradicting the assumption in the original PRD. Verified via `az containerapp exec ... -- export`: the platform instead injects Azure-specific per-signal vars (`CONTAINERAPP_OTEL_TRACING_GRPC_ENDPOINT`, `CONTAINERAPP_OTEL_METRIC_GRPC_ENDPOINT`, `CONTAINERAPP_OTEL_LOGGING_GRPC_ENDPOINT`), pointing at an internal collector address (`http://k8se-otel.k8se-apps.svc.cluster.local:4317/v1/traces`). `TelemetrySetup.cs` now falls back to these vars when `OTEL_EXPORTER_OTLP_ENDPOINT` is absent, so the app resolves a sensible-looking endpoint (confirmed in the startup log).
  2. Even with that endpoint correctly resolved and the environment's `openTelemetryConfiguration.destinationsConfiguration.dataDogConfiguration` correctly set (site + key, confirmed via direct ARM GET), **zero signal of any kind** — not traces, not logs — reaches Datadog from this app. This rules out a traces-specific bug; either the managed collector isn't forwarding to Datadog for this environment, the API key didn't actually persist despite the ARM write succeeding (ARM returns this field as `null` on every GET regardless of whether it's populated, so this can't be confirmed from the API), or there's a connectivity/DNS issue reaching the internal collector from this app's pod. Not yet root-caused.

**Customer-facing takeaway**: the sidecar path is the more reliable, more debuggable, and better-documented integration today — get the OTLP protocol right and it works exactly as advertised, with rich per-app tagging. The managed-agent path's promise (zero extra containers, config lives entirely at the environment level) doesn't hold up in practice yet: it required reverse-engineering undocumented env var names, and even then produced no visible signal in Datadog. This matches the risk flagged in the original PRD before any code was written — the managed-agent path is real, but immature enough that the sidecar path should be recommended for customers wanting a demo-ready path today.

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
