---
title: Refresh disaster declarations
description: Follow daily OpenFEMA declaration ingestion and preserve the distinction between historical declarations and current hazards
docType: reference
audience:
  - data-engineer
maturity: stable
verifiedOn: 2026-09-01
sidebar:
  label: FEMA disaster refresh
---

**Pipeline:** `pl-fema-refresh` · **Schedule:** daily 02:00 UTC · **Source:** OpenFEMA `DisasterDeclarationsSummaries`

The pipeline retrieves declarations dated from 2010 onward across US states and territories. It writes a raw Parquet snapshot and normalized JSON Lines records, then creates environmental-domain Search documents.

## Flow

1. `fetch-and-store-fema` Function Activity pages the OpenFEMA API with `$skip`/`$top`, ordered by declaration date.
2. It writes the raw Parquet archive to `raw-data/` and the normalized records to `prepared-data/`, returning that blob path as a pipeline parameter.
3. `index-search-shared` reads the prepared records, builds narratives from declaration number/type, incident, area, dates, and program flags.
4. Token-chunk (tiktoken, 512/64-overlap), embed, and upsert stable documents.

## Interpretation boundary

A federal declaration is historical administrative evidence. It does not prove current conditions, parcel-level risk, damage totals, or local declarations outside FEMA's dataset. Preserve null incident-end or closeout dates rather than interpreting them as a current emergency.

## Verify a run

- All records satisfy the configured date boundary.
- Pagination does not duplicate or skip the page transition.
- Declaration identity and designated area survive normalization.
- Search content distinguishes declaration type from incident type.
- Function logs retain counts and run identity without serializing declaration payloads.

For request-time use, see `get_disaster_history` in the [MCP tool guide](/infra-advisor-ai/services/mcp-tools/).
