---
title: Refresh Texas water planning data
description: Retired during the Airflow-to-ADF migration — no automated ingestion for this source exists today
docType: reference
audience:
  - data-engineer
  - security-engineer
maturity: deprecated
verifiedOn: 2026-09-01
sidebar:
  label: TWDB & EPA water refresh
---

**Status:** retired, not migrated. This DAG (`twdb_water_plan_refresh`, monthly) does not exist in the Azure Data Factory pipelines that replaced Airflow. See the [migration notes](/agent-guides/airflow-to-adf-migration/) for why.

## What this covered

The original DAG combined two independent evidence sources: a configured TWDB planning workbook (reviewed direct HTTPS ZIP URL, with zip-bomb/path-traversal/encryption validation before parsing) and EPA SDWIS Texas water-system data. They converged in the `water` Search domain as `water_plan_project` and `water_system_record` documents respectively.

This was the most complex of the original DAGs — the validation graph alone (host/redirect checks, archive entry count and paths, compression ratio, encryption state, nested XLSX package inspection) was judged not worth reimplementing for a demo environment during the migration to Azure Data Factory.

## Current state

- **No pipeline refreshes this data going forward.** Neither the TWDB workbook parsing nor the EPA SDWIS fetch has an Azure Data Factory / Azure Functions equivalent.
- **Previously-indexed documents remain in Azure AI Search untouched** — `water_plan_project` and `water_system_record` documents from the last Airflow run are still queryable; they simply stop being refreshed.
- If this data needs to come back, it would be a new `services/adf-functions` domain module + Data Factory pipeline, built the same way the six migrated domains were (see the [pipeline architecture](/data-pipeline/)) — not a restoration of the old DAG's code, which depended on Airflow-specific patterns (XCom manifest, DagBag scheduling) that no longer exist in this repo.
