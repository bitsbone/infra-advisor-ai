---
title: Initialize the synthetic knowledge corpus
description: Retired during the Airflow-to-ADF migration — the synthetic corpus already indexed has no automated regeneration path today
docType: guide
audience:
  - data-engineer
  - maintainer
maturity: deprecated
verifiedOn: 2026-09-01
sidebar:
  label: Knowledge base init
---

**Status:** retired, not migrated. This DAG (`knowledge_base_init`, manual trigger only) does not exist in the Azure Data Factory pipelines that replaced Airflow. See the [migration notes](/agent-guides/airflow-to-adf-migration/) for why.

## What this covered

The original DAG ran an image-bundled generation script (`generate_synthetic_docs.py`) that created a fictional firm knowledge corpus — proposals, lessons learned, cost benchmarks, risk frameworks, and funding guides — and indexed it into Azure AI Search, purely to demonstrate retrieval over internal-style knowledge without publishing real consulting documents. It was already a one-time bootstrap by design (idempotent — skipped paid regeneration once the expected corpus existed), not a routine scheduled ingestion, which made it a natural one to drop rather than migrate.

## Current state

- **No pipeline regenerates or expands this corpus.** `generate_synthetic_docs.py` was removed along with the rest of `services/ingestion/`; there is no Azure Functions equivalent.
- **The synthetic documents already indexed from the last Airflow run remain in Azure AI Search** and are still retrievable via `search_project_knowledge` — every downstream explanation should still identify this material as synthetic, not evidence of actual project experience, costs, or policy.
- If the Azure AI Search index or schema is missing entirely (not just this corpus), that's a separate infrastructure problem — see `services/adf-functions/scripts/create_search_index.py`, which creates the index schema (but does not generate any synthetic documents).
- Regenerating or expanding the synthetic corpus today requires a manual one-off script run against Azure AI Search directly; there is no scheduled or triggerable pipeline for it.
