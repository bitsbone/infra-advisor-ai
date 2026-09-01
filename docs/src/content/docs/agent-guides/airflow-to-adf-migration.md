---
title: Airflow to Azure Data Factory migration
description: Why self-hosted Airflow was retired, what moved to ADF + Azure Functions, and what was dropped rather than migrated
docType: maintainer
audience:
  - maintainer
  - data-engineer
maturity: stable
verifiedOn: 2026-09-01
sidebar:
  label: Airflow → ADF Migration
---

## Status

Done. Airflow (Helm release, DAGs, CI workflow, Makefile targets) has been removed from the repository. The live Helm release/namespace/secrets on AKS are decommissioned separately, on request — this repo's code no longer references or deploys them.

## Why

The project's only stated reason for choosing Airflow specifically (per this PRD's original tech-choice table: "Required for DJM story") was to have an orchestrator Datadog's Data Jobs Monitoring could demo against — DJM originally integrated with Airflow and Spark only. Once Datadog added native Data Jobs Monitoring support for Azure Data Factory, the reason to keep operating a self-hosted Airflow-on-AKS deployment (Helm release, its own metadata database, image-contract verification, Helm upgrade/rollback machinery) went away. A full evening was also spent that session fighting a self-hosted Airflow deployment that had been silently broken for 40 days (stuck Helm release, immutable StatefulSet drift from a deleted PVC, a `check_migrations` bug that false-failed despite matching migration heads) — reinforcing that the operational cost wasn't buying anything the project needed.

## What moved

Six of the eight ingestion DAGs migrated to Azure Data Factory pipelines triggering Azure Functions (`services/adf-functions/`, Consumption plan — no persistent orchestrator to operate):

| Old Airflow DAG | New ADF pipeline |
|---|---|
| `nbi_refresh` | `pl-nbi-refresh` |
| `fema_refresh` | `pl-fema-refresh` |
| `eia_refresh` | `pl-eia-refresh` |
| `samgov_awards_refresh` | `pl-samgov-awards-refresh` |
| `census_market_intelligence_refresh` | `pl-census-market-intelligence-refresh` |
| `public_docs_ingestion` | `pl-public-docs-ingestion` |

Each pipeline is a Schedule Trigger driving two Function Activities (`fetch-and-store-<domain>` → `index-search-shared`) instead of Airflow's fetch→store→index task chain. Blob paths pass between activities as plain pipeline parameters — the XCom-manifest-checksum layer (`_blob_manifest.py`) existed solely to work around Airflow's metadata-DB size limits, and ADF Function Activity outputs have no equivalent constraint, so it was dropped entirely rather than ported. Chunking was also standardized to tiktoken (512 tokens / 64-token overlap) across every domain — the original NBI DAG used raw 500-character chunks and the SAM.gov DAG used no chunk overlap; expect document-count differences in those two domains against the old Airflow-produced index as the deliberate result of that fix, not a regression.

## What was dropped, not migrated

Two source families were retired entirely rather than ported, since neither carries its weight in a demo environment:

- **`twdb_water_plan_refresh`** — the most complex of the original DAGs (zip-bomb/path-traversal/encryption validation before parsing a TWDB Excel workbook, fan-in/fan-out graph joining it with EPA SDWIS data). No ADF pipeline exists for it.
- **`knowledge_base_init`** — a one-time synthetic-document-generation bootstrap (~80 sequential LLM calls), already run at least once and idempotent by design. No automated regeneration path exists today.

**Existing indexed data for both stays in Azure AI Search untouched** — `water_plan_project`, `water_system_record`, and `source: "synthetic"` documents from the last Airflow run remain queryable via the MCP tools; they simply stop being refreshed going forward. `spark_feature_engineering.py` was already disabled (`.airflowignore`) before this migration and was never a live concern either way.

## Observability changes

- **Data Jobs Monitoring**: OpenLineage-based (Airflow) → a pull-based Azure integration that polls the ARM API for ADF pipeline/activity/trigger run status. Manual one-time setup (custom least-privilege role + Datadog UI configuration) — see [`ops/azure/README.md`](https://github.com/bitsbone/infra-advisor-ai/blob/main/ops/azure/README.md). It's a **preview** feature; dataset lineage doesn't resolve for the custom Function steps (expected, not a bug).
- **APM**: Airflow's bespoke `_dd_blob.py`/`_dd_logging.py` ddtrace wiring → `ddtrace.auto` + `datadog-serverless-compat` (agentless — Consumption-plan Functions have no Agent sidecar to send traces to).
- **LLM Observability**: newly added for the embedding calls in `index-search-shared` (`LLMObs.embedding()` spans, tagged with chunk/vector counts only) — Airflow's ingestion DAGs never had this.
- Two Datadog dashboard assets (`datadog/dashboards/pipeline-health.json`, `datadog/dashboards/blob-storage.json`) still carry Airflow-era widgets/queries keyed on metrics and span names (`airflow.dag_run.*`, the custom `azure.blob.upload` span) that the new Functions don't emit — flagged inline in those files and in [Dashboards](/observability/dashboards/) as not yet rebuilt.

## Where the old content went

- Code: `services/ingestion/` (the Airflow app), `k8s/airflow/values.yaml`, `.github/workflows/deploy-airflow.yml`, and ~15 Airflow-specific Makefile targets were deleted outright — recoverable from git history if needed for archaeology.
- Docs: every public-docs page that described Airflow as the live architecture (`data-pipeline/*`, `deployment/kubernetes`, `deployment/quickstart`, `development/conventions`, `development/testing`, and passing mentions across `architecture/*`/`observability/*`) was rewritten to describe the ADF/Functions equivalent, or — for the two dropped DAGs — rewritten to explicitly say so rather than inventing an ADF pipeline that doesn't exist.
- The original product spec (`specs/infraadvisor-prd.md`) was updated in place rather than left as an untouched historical snapshot, since it's referenced as a living spec elsewhere in this repo's docs.
