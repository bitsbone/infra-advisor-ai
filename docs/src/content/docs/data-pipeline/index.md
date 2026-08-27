---
title: Data Pipeline
description: Airflow DAGs and data ingestion pipeline for InfraAdvisor AI
---

The repository defines nine Apache Airflow DAGs for real US government data, raw Parquet storage in Azure Blob Storage, and searchable chunks in Azure AI Search. Eight are included in the current runtime; the Spark feature-engineering DAG remains deliberately disabled until it has an explicit Java/PySpark task image or a lighter replacement. Together the enabled pipelines maintain the infrastructure knowledge base used by the AI application.

## How the pipeline works

The enabled dataset DAGs follow the same three-stage pattern, but record collections never pass through Airflow's metadata database:

```
Task 1: fetch_*_data()
  Paginate external government API
  Serialize normalized records as deterministic JSON Lines
  Upload records to Azure Blob Storage
  XCom push: versioned records_manifest only

Task 2: store_raw_parquet()
  XCom pull manifest → verify and download records
  Records → Pandas DataFrame → Parquet bytes
  Upload to Azure Blob Storage via dd_upload_blob()
  XCom push: versioned parquet_manifest only
  (emits azure.blob.upload APM span with blob.size_bytes metric)

Task 3: index_to_search()
  XCom pull manifest → verify and download records
  For each record:
    1. Generate narrative text
    2. Chunk (character window or tiktoken 512-token / 64-overlap)
    3. Embed: Azure OpenAI text-embedding-3-small → 1536-dim vector
    4. Build Azure AI Search document {id, content, content_vector, source, domain, …}
  Upsert in 100-doc batches
```

## Durable task handoff contract

Airflow XCom is control-plane metadata, not a bulk-data transport. FEMA, NBI, EIA, USASpending, Census, and TWDB fetch tasks therefore stage normalized JSON Lines in the private `raw-data` Blob container and exchange only a small manifest with downstream tasks. Census and TWDB retain their independent source branches and use a distinct manifest for each branch before fan-in.

The reusable implementation is [`services/ingestion/dags/_blob_manifest.py`](https://github.com/kyletaylored/infra-advisor-ai/blob/main/services/ingestion/dags/_blob_manifest.py). Each manifest has this shape:

```json
{
  "schema_version": "1.0",
  "source": "openfema.disaster_declarations",
  "run_id": "scheduled__2026-08-26T02:00:00+00:00",
  "blob": {
    "container": "raw-data",
    "path": "fema/manifests/declarations_scheduled_..._a1b2c3d4e5f6.jsonl"
  },
  "record_count": 1250,
  "checksum": {
    "algorithm": "sha256",
    "value": "<64-character digest>"
  },
  "content_type": "application/x-ndjson",
  "content_encoding": "utf-8"
}
```

The manifest contains container and object names rather than a URL, SAS token, connection string, or provider payload. Consumers reject unknown schema versions, unexpected fields, URLs, query strings, source mismatches, unsupported encodings, checksum mismatches, and record-count mismatches before indexing. Blob paths are deterministic for an Airflow run ID, so a retry overwrites the same staging object instead of creating an unbounded set of duplicates.

The fetch task still returns an integer record count as its normal PythonOperator return value, which is safe for XCom and useful in the Airflow UI. The disabled Spark experiment passes only two local path strings and remains excluded from the deployed DagBag.

## DAG schedule summary

| DAG | Schedule | Source | Volume |
|-----|----------|--------|--------|
| [fema_refresh](/infra-advisor-ai/data-pipeline/fema-refresh/) | Daily 02:00 UTC | OpenFEMA REST | Declarations since 2010 |
| [nbi_refresh](/infra-advisor-ai/data-pipeline/nbi-refresh/) | Weekly Sun 03:00 UTC | FHWA NBI ArcGIS | 615k+ TX bridges |
| [eia_refresh](/infra-advisor-ai/data-pipeline/eia-refresh/) | Weekly 04:00 UTC | EIA API v2 | State generation/capacity |
| [twdb_water_plan_refresh](/infra-advisor-ai/data-pipeline/twdb-refresh/) | Monthly 1st 05:00 UTC | TWDB 2027 ZIP workbook + EPA SDWIS | State water plan projects + Texas water systems |
| [knowledge_base_init](/infra-advisor-ai/data-pipeline/knowledge-base-init/) | On-demand | LLM synthetic generation | Firm knowledge documents |
| `samgov_awards_refresh` | Daily | USASpending.gov API | Federal contract awards ≥ $500K from SAM.gov |
| `census_market_intelligence_refresh` | Weekly | Census Bureau | Census Bureau market intelligence data |
| `public_docs_ingestion` | On-demand | Public infrastructure documents | Public infrastructure document ingestion into knowledge base |
| `spark_feature_engineering` | Disabled | Ingested datasets | Requires a dedicated Spark runtime or lighter replacement |

## Azure AI Search index domains

All DAGs write to the single `infra-advisor-knowledge` index. Documents are tagged by `domain` for filtered search:

| Domain | Source DAGs | Example document types |
|--------|------------|----------------------|
| `transportation` | nbi_refresh | Bridge condition records |
| `environmental` | fema_refresh | Disaster declarations |
| `energy` | eia_refresh | State electricity statistics |
| `water` | twdb_water_plan_refresh | Water plan projects, water system records |
| `business_development` | samgov_awards_refresh, census_market_intelligence_refresh | Contract awards, market data |
| `synthetic` | knowledge_base_init | Firm proposals, lessons learned, cost guides |

## Airflow setup

The Airflow scheduler runs as a `StatefulSet` (`airflow-scheduler-0`) in the `airflow` namespace, using **LocalExecutor** so tasks run as subprocesses inside the scheduler pod. Airflow, Python, providers, Parquet support, DAGs, and helper scripts are delivered together in the pinned image defined by `services/ingestion/Dockerfile`. Runtime pods do not install dependencies or receive DAGs through `kubectl cp`, which keeps the scheduler, DAG processor, migration jobs, and task subprocesses on one reproducible revision.

The public demo routes the Airflow UI through `/airflow`. Credentials come from the deployment secret and are never documented, committed, or defaulted to a shared educational password. The chart's `registry.secretName` points every Airflow workload and hook job at `ghcr-pull-secret` in the `airflow` namespace; create or rotate that namespace-scoped credential with `make create-airflow-ghcr-secret` before install or upgrade.

**Build and verify locally:**

```bash
make test-airflow
make test-airflow-container
```

`make test-airflow` runs the ingestion suite and a real `DagBag` import in an isolated local Airflow home. The container check additionally proves that the deployed image contains `pyarrow`, all enabled DAGs, and the scripts referenced by `knowledge_base_init` and `public_docs_ingestion`. GitHub Actions builds the Airflow image locally, runs this same contract inside that exact SHA-tagged image, and only then pushes the SHA and `latest` tags; the Helm deployment job depends on that verified publish job.

**Build and roll out an immutable revision:**

```bash
make build-airflow-image
# Push an organization-controlled immutable tag, verify the current release, then:
make create-airflow-ghcr-secret
make preflight-airflow-cluster
make upgrade-airflow AIRFLOW_IMAGE_TAG=<git-commit-sha>
```

All DAGs remain paused when first created. After the image and external dependencies pass a canary, trigger or unpause only the named DAGs approved for recurring execution. `spark_feature_engineering` is intentionally excluded from the image's DagBag until it has a separate Java/PySpark runtime or is replaced with a lighter transform.

The cluster preflight is deliberately read-only and fails unless Helm reports a deployed release, every desired workload and live pod uses one Airflow image, both the application and GHCR pull secrets are valid, the metadata database and migrations are current, the Blob connection is not the educational placeholder, and the Airflow secret includes the SAM.gov key required by the awards DAG. The upgrade command also pulls the exact requested image and runs `verify_image_contract.py` before changing the release, then repeats the cluster preflight against the expected immutable image afterward. It never automatically rolls back a failed or pending release because repeated atomic upgrades can leave Deployment and StatefulSet pods on different Airflow/schema versions.

If the preflight reports a failed release, first take an organization-approved PostgreSQL metadata backup and retain the existing logs PVC. Inspect `helm history airflow -n airflow`, choose an explicitly reviewed last-known-good revision, and recover the release to one coherent image before attempting the custom-image canary. Refresh `airflow-azure-secret` and `ghcr-pull-secret` from local or CI secret storage with `make create-airflow-secret` and `make create-airflow-ghcr-secret`; these targets use environment variables and never commit credentials. Routine install, deploy, and upgrade targets never uninstall the Helm release or delete its namespace. The only target containing those destructive operations is `make recover-airflow-destructive`, which refuses to run unless `AIRFLOW_DESTRUCTIVE_RECOVERY=delete-airflow-release-and-namespace` is supplied after the operator has backed up or deliberately discarded the metadata and logs.

The container runtime contract verifies that both `_blob_manifest.py` and `_dd_blob.py` are present beside the deployed DAGs, checks every installed distribution requirement for version conflicts, imports the real Airflow 3.2.1 DagBag, and requires the expected eight enabled DAGs. DAGs import `PythonOperator` from the Airflow 3 standard provider rather than the deprecated Airflow 2 compatibility path. Airflow 3 settings use `AIRFLOW__DAG_PROCESSOR__MIN_FILE_PROCESS_INTERVAL` for the two-minute scan interval and a three-minute scheduler health threshold so the one-minute health probe does not declare a healthy but deliberately slower scheduler stale. Unit tests exercise manifest round trips, retry-stable paths, tamper detection, source and count mismatches, credential-bearing reference rejection, TWDB ZIP/XLSX validation, and a static guard that prevents enabled DAGs from reintroducing record arrays in XCom.

## Datadog Data Jobs Monitoring

All DAGs emit OpenLineage events to Datadog DJM via the Airflow OpenLineage provider:

```
AIRFLOW__LINEAGE__BACKEND=openlineage.lineage_backend.OpenLineageBackend
OPENLINEAGE__TRANSPORT__TYPE=datadog
```

Navigate to **Datadog → Data Observability → Data Jobs** to see run duration, task status, and lineage graphs for every DAG execution.

## Log-trace correlation

The scheduler uses a custom `DDJsonFormatter` (in `airflowLocalSettings`) that outputs structured JSON task logs with `dd.trace_id` and `dd.span_id` fields. `sitecustomize.py` in the DAGs folder ensures ddtrace is initialized in every LocalExecutor task subprocess, so task logs carry trace IDs even when tasks run in separate Python processes.

The Blob helper emits `azure.blob.upload` spans tagged with the DAG ID, private container name, deterministic object path, and payload byte count. Application logs report record counts and storage operations but do not serialize fetched provider records, connection strings, SAS tokens, or source URL query parameters. This gives the demo operational evidence for fetch volume and durable handoff without copying source data into telemetry.

## Sections in this chapter

- [NBI Bridge Refresh](/infra-advisor-ai/data-pipeline/nbi-refresh/) — 615k+ TX bridges weekly from FHWA, exact field names, condition codes
- [FEMA Disaster Refresh](/infra-advisor-ai/data-pipeline/fema-refresh/) — Daily disaster declarations from OpenFEMA, token chunking
- [EIA Energy Refresh](/infra-advisor-ai/data-pipeline/eia-refresh/) — Weekly state electricity generation/capacity from EIA API v2
- [TWDB Water Plan Refresh](/infra-advisor-ai/data-pipeline/twdb-refresh/) — Monthly TWDB Excel + EPA SDWIS water systems
- [Knowledge Base Init](/infra-advisor-ai/data-pipeline/knowledge-base-init/) — LLM-generated synthetic firm knowledge, on-demand trigger
