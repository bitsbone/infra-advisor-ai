---
title: Refresh Texas bridge data
description: Follow the weekly FHWA NBI ingestion from ArcGIS records to transportation search documents
docType: reference
audience:
  - data-engineer
maturity: stable
verifiedOn: 2026-09-01
sidebar:
  label: NBI bridge refresh
---

**Pipeline:** `pl-nbi-refresh` · **Schedule:** Sunday 03:00 UTC · **Source:** FHWA NBI ArcGIS service

The pipeline retrieves Texas records (`STATE_CODE_001='48'`) with a non-null sufficiency rating, writes a raw Parquet snapshot, and upserts transportation documents into Azure AI Search.

## Source-to-index path

1. `fetch-and-store-nbi` pages the ArcGIS feature layer and flattens each feature's `attributes` object.
2. It writes the raw Parquet snapshot to `raw-data/` and normalized JSON Lines records to `prepared-data/`, returning that blob path as a pipeline parameter — no manifest/XCom equivalent needed.
3. `index-search-shared` builds a bounded narrative and embedding for each bridge record, using the shared tiktoken chunker (512 tokens / 64 overlap) — this replaced the original DAG's bespoke 500-character chunker.

The code requests exact FHWA fields such as structure number, facility, location, state/county codes, traffic, condition ratings, structurally-deficient flag, sufficiency, inspection date, year built, and coordinates. Preserve those raw names at the provider boundary.

## Interpretation boundary

Deck, superstructure, and substructure ratings use NBI condition codes from failed through excellent. "Structurally deficient," sufficiency rating, traffic, and scour/inspection concepts are separate fields; do not collapse them into one invented risk score.

The dataset is Texas-only in this pipeline even though the MCP tool can query national NBI data directly. Avoid repeating the national bridge count as the Texas ingestion volume.

## Verify a run

- The fetch count is non-zero and every record matches state code 48.
- Search documents use the transportation domain and stable source identity.
- A sampled structure preserves its source ratings and identifier.
- Logs and spans contain counts/paths but not full record arrays or signed credentials.
- Chunk/document counts differ from the pre-migration Airflow-produced index — expected, from the chunking standardization, not a regression.

See [MCP tool selection](/infra-advisor-ai/services/mcp-tools/#bridge-versus-txdot) for request-time bridge use.
