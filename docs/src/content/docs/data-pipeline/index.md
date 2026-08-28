---
title: Data pipeline
description: Understand how Airflow moves governed source data through private storage into a derived search index
docType: concept
audience:
  - data-engineer
  - application-developer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 7
---

Airflow refreshes public infrastructure data, preserves source snapshots in private Blob Storage, and builds documents for Azure AI Search. The search index is a serving layer; the staged source data and manifests make each run explainable and reproducible.

## Shared ingestion pattern

```text
provider API or reviewed file
  → validate and normalize records
  → write deterministic JSONL staging object
  → XCom small versioned manifest
  → verify checksum/count/source and read records
  ├─ write Parquet snapshot
  └─ create narratives → chunk → embed → upsert Search
```

Bulk records do not travel through Airflow's metadata database. XCom carries only control metadata: container, object path, source, run ID, count, checksum, content type, and schema version.

## Why the manifest matters

`_blob_manifest.py` gives task boundaries a verifiable contract. Consumers reject unknown versions or fields, URLs and query strings, source mismatches, unsupported encodings, checksum failures, and record-count failures. Blob paths are deterministic per run, so retries overwrite the same staging object rather than creating unlimited duplicates.

The manifest contains no connection string, SAS token, or provider body. Logs can identify the run and object without exposing credentials.

## Enabled source families

| DAG | Cadence in source | Serving domain |
|---|---|---|
| `fema_refresh` | Daily | environmental |
| `nbi_refresh` | Weekly | transportation |
| `eia_refresh` | Weekly | energy |
| `twdb_water_plan_refresh` | Monthly | water |
| `samgov_awards_refresh` | Weekly | business development |
| `census_market_intelligence_refresh` | Monthly | business development |
| `public_docs_ingestion` | Weekly | knowledge corpus |
| `knowledge_base_init` | Manual | synthetic demonstration corpus |

`spark_feature_engineering.py` remains outside the deployed DagBag until it has an explicit compatible runtime or replacement. A file in the DAG directory is not automatically a shipped pipeline.

## Reproducible Airflow runtime

DAGs and helper scripts ship inside one immutable Airflow image. The scheduler, DAG processor, API server, task processes, migrations, and hooks must agree on that image and metadata schema. Runtime package installation, git-sync, and copying DAGs into pods are intentionally disabled.

Verification has two gates:

```bash
make test-airflow
make test-airflow-container
```

The first runs ingestion tests and a real DagBag. The second proves the built image contains its dependencies, helpers, and enabled DAGs. Cluster upgrade targets pull and verify the exact immutable image before Helm mutation.

## Datadog evidence

OpenLineage sends DAG/task lifecycle to Data Jobs Monitoring. Task processes emit trace-correlated JSON logs. `_dd_blob.py` creates `azure.blob.upload` spans with bounded container, object, DAG, and size fields.

Operational telemetry must not serialize record arrays, workbook rows, provider bodies, connection strings, SAS tokens, or source query parameters.

## Investigate a run

1. Find the DAG run and failing task in Data Jobs Monitoring or Airflow.
2. Follow its trace-correlated logs.
3. Locate the manifest object and verify schema, source, count, and checksum.
4. Confirm the Parquet snapshot and Search upsert belong to the same run.
5. Compare indexed document counts and sample narratives without logging full source data.

Continue to the source-specific pages for NBI, FEMA, EIA, TWDB/EPA, and synthetic initialization.
