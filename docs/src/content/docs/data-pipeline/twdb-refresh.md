---
title: Refresh Texas water planning data
description: Ingest a reviewed TWDB workbook and EPA water-system records through separate validated branches
docType: reference
audience:
  - data-engineer
  - security-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  label: TWDB & EPA water refresh
---

**DAG:** `twdb_water_plan_refresh` · **Schedule:** monthly, day 1 at 05:00 UTC

The DAG combines two independent evidence sources: a configured TWDB planning workbook and EPA SDWIS Texas water-system data. They converge in the water Search domain but retain separate manifests, source identities, and document types.

## TWDB file boundary

The deployment points to a reviewed direct HTTPS ZIP URL. The fetch validates host/redirect, content type and size, archive entry count and paths, compression ratio, encryption state, and the nested XLSX package before storing the raw source.

The parser searches only a bounded number of header rows for a recognized project-name schema, then maps the reviewed infrastructure-project worksheet into stable fields such as project name, sponsor, region, recommendation, components, capital cost, and online decade.

If the workbook contains no recognized project records, the task fails before publishing a normalized manifest or updating Search. Missing county, volume, or decade-specific data remains absent; the narrative does not invent it.

## EPA branch

The SDWIS branch retrieves Texas community water-system records, stages normalized JSON Lines and Parquet independently, and builds `water_system_record` documents from identity, type, population, activity, source-water, and available violation fields.

## Fan-in and verification

- Each branch has its own source-qualified manifest and checksum.
- A TWDB failure does not become a fabricated EPA result, or vice versa.
- Search documents retain `water_plan_project` versus `water_system_record` identity.
- Sampled narratives contain only populated source fields and correct units.
- Logs record delivery shape, bytes, sheets, counts, and storage operations—not workbook rows, response bodies, or credentials.

The direct workbook URL is configuration, not discovered by scraping the landing page at runtime. Review it whenever TWDB changes its publication format.
