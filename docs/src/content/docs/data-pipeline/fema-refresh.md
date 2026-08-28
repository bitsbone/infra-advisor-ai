---
title: Refresh disaster declarations
description: Follow daily OpenFEMA declaration ingestion and preserve the distinction between historical declarations and current hazards
docType: reference
audience:
  - data-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  label: FEMA disaster refresh
---

**DAG:** `fema_refresh` · **Schedule:** daily 02:00 UTC · **Source:** OpenFEMA `DisasterDeclarationsSummaries`

The DAG retrieves declarations dated from 2010 onward across US states and territories. It stages verified JSON Lines, writes a Parquet snapshot, then creates environmental-domain Search documents.

## Flow

1. Page the OpenFEMA API with `$skip`/`$top`, ordered by declaration date.
2. Write the complete normalized collection through the shared manifest helper.
3. Verify the manifest before Parquet conversion and indexing.
4. Build narratives from declaration number/type, incident, area, dates, and program flags.
5. Token-chunk, embed, and upsert stable documents.

## Interpretation boundary

A federal declaration is historical administrative evidence. It does not prove current conditions, parcel-level risk, damage totals, or local declarations outside FEMA's dataset. Preserve null incident-end or closeout dates rather than interpreting them as a current emergency.

## Verify a run

- All records satisfy the configured date boundary.
- Pagination does not duplicate or skip the page transition.
- Declaration identity and designated area survive normalization.
- Search content distinguishes declaration type from incident type.
- Task logs retain counts and run identity without serializing declaration payloads.

For request-time use, see `get_disaster_history` in the [MCP tool guide](/infra-advisor-ai/services/mcp-tools/).
