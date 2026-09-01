---
title: Refresh regional energy data
description: Follow weekly EIA operational data for the configured southeastern-state cohort
docType: reference
audience:
  - data-engineer
maturity: stable
verifiedOn: 2026-09-01
sidebar:
  label: EIA energy refresh
---

**Pipeline:** `pl-eia-refresh` · **Schedule:** Sunday 04:00 UTC · **Source:** EIA electric-power operational data API

The deployed pipeline is not an all-state pipeline. It requests annual generation and capacity for the configured southeastern cohort: FL, GA, AL, MS, LA, TX, AR, TN, SC, NC, and VA.

## Flow

1. `fetch-and-store-eia` fetches each state separately with EIA API pagination and the requested generation/capacity fields.
2. It adds the requested state code to each record, writes a raw Parquet snapshot to `raw-data/`, and writes normalized records to `prepared-data/`.
3. `index-search-shared` creates narratives using source period, state, sector, fuel description, values, and source-provided units, then token-chunks, embeds, and upserts energy-domain documents.

## Interpretation boundary

Keep generation and capacity distinct and preserve their source units. The dataset does not provide project capital cost, asset vulnerability, plant-age analysis, or a complete national comparison in its current configured scope.

## Verify a run

- Every requested state appears and no unconfigured state is implied.
- Pagination stops from provider totals/empty pages without duplicating offsets.
- Generation and capacity use their corresponding source units.
- Search documents carry energy domain and EIA source identity.
- `EIA_API_KEY` is supplied through the Function App's app settings and absent from URLs/logs.
