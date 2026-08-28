---
title: Refresh Texas bridge data
description: Follow the weekly FHWA NBI ingestion from ArcGIS records to transportation search documents
docType: reference
audience:
  - data-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  label: NBI bridge refresh
---

**DAG:** `nbi_refresh` · **Schedule:** Sunday 03:00 UTC · **Source:** FHWA NBI ArcGIS service

The DAG retrieves Texas records (`STATE_CODE_001='48'`) with a non-null sufficiency rating, stages them through the shared manifest contract, writes Parquet, and upserts transportation documents into Azure AI Search.

## Source-to-index path

1. `fetch_nbi_data` pages the ArcGIS feature layer and flattens each feature's `attributes` object.
2. It writes JSON Lines under `nbi/texas/manifests` and pushes only the manifest through XCom.
3. `store_raw_parquet` verifies the manifest and writes a retry-stable Parquet snapshot.
4. `index_to_search` creates a bounded narrative and embedding for each bridge record.

The code requests exact FHWA fields such as structure number, facility, location, state/county codes, traffic, condition ratings, structurally-deficient flag, sufficiency, inspection date, year built, and coordinates. Preserve those raw names at the provider boundary.

## Interpretation boundary

Deck, superstructure, and substructure ratings use NBI condition codes from failed through excellent. “Structurally deficient,” sufficiency rating, traffic, and scour/inspection concepts are separate fields; do not collapse them into one invented risk score.

The dataset is Texas-only in this DAG even though the MCP tool can query national NBI data directly. Avoid repeating the national bridge count as the Texas ingestion volume.

## Verify a run

- The fetch count is non-zero and every record matches state code 48.
- The manifest count/checksum passes before Parquet or indexing.
- Search documents use the transportation domain and stable source identity.
- A sampled structure preserves its source ratings and identifier.
- Logs and spans contain counts/paths but not full record arrays or signed credentials.

See [MCP tool selection](/infra-advisor-ai/services/mcp-tools/#bridge-versus-txdot) for request-time bridge use.
