---
title: TWDB Water Plan Refresh
description: Texas Water Development Board and EPA SDWIS monthly water infrastructure ingestion
---

**DAG ID:** `twdb_water_plan_refresh`  
**Schedule:** Monthly, 1st of month 05:00 UTC  
**Data sources:**
1. [TWDB 2027 State Water Plan data](https://www.twdb.texas.gov/waterplanning/data/rwp-database/index.asp) — Texas Water Development Board ZIP containing the official Excel summary workbook
2. [EPA SDWIS](https://enviro.epa.gov/enviro/efservice) — Safe Drinking Water Information System

**Coverage:** Texas community water systems and regional water plan projects

## Purpose

Water infrastructure planning is one of the most pressing infrastructure challenges in Texas. This DAG provides data for queries like:
- "What are the largest water supply projects planned for the Panhandle region?"
- "List Texas water utilities with health-based violations"
- "Show water plan projects with costs exceeding $500M in the 2050 planning horizon"

## Task structure

```
fetch_twdb_workbook
  └── HTTP GET: direct TWDB ZIP workbook URL
  └── Validate HTTPS host, status, content type, size, archive paths, entry count, compression ratio, and the nested XLSX package
  └── Upload raw ZIP: raw-data/twdb/.../twdb_water_plan_*.zip
  └── Upload normalized project records as JSON Lines and XCom push only the versioned manifest

fetch_epa_sdwis
  └── GET https://enviro.epa.gov/enviro/efservice/WATER_SYSTEM/STATE_CODE/TX/PWS_TYPE_CODE/CWS/JSON
  └── Upload normalized records as JSON Lines plus a raw Parquet snapshot
  └── XCom push only versioned records and Parquet manifests

index_to_search (two tasks)
  └── TWDB: token-chunk project narratives → embed → upsert
       document_type: water_plan_project, domain: water
  └── SDWIS: water system records → embed → upsert
       document_type: water_system_record, domain: water
```

## TWDB data fields

The current workbook places explanatory rows before the real headers. The DAG scans only the first 25 rows of each worksheet for an exact normalized project-name header, collapses embedded whitespace and newlines, and maps the `WMSInfrastructureProjects` columns into a stable record. It does not scrape the TWDB HTML landing page or infer an arbitrary download link at runtime; the deployment configuration names the reviewed direct agency ZIP URL.

| Canonical field | Description |
|----------------|-------------|
| `project_name` | Name of water supply project |
| `project_sponsor` | Project sponsor list |
| `county` | County where project is located |
| `region` | Regional water planning area (A–P) |
| `supply_type` | Groundwater, surface water, conservation, reuse, etc. |
| `strategy_type` | New supply, demand management, infrastructure improvement |
| `recommendation_type` | Published recommendation classification |
| `project_components` | Semicolon-delimited infrastructure components |
| `capital_cost` | Published project capital cost ($) |
| `decade_of_need` | Published online decade |
| `cost_2030`–`cost_2080` | Compatibility fields; the published capital cost is assigned to its online decade |

The fetch fails closed on an HTML/error response, a non-TWDB redirect, an oversized response, malformed ZIP/XLSX data, traversal or encrypted archive entries, an unsafe compression ratio, or any archive that does not contain exactly one workbook. HTTP and archive checks happen before the raw source is written to Blob Storage; a separate schema guard fails the task before the normalized manifest or search index can be updated when no recognized project records are present.

Search narratives include only populated source fields. For example, the current infrastructure-project worksheet publishes sponsor, recommendation, components, capital cost, and online decade but not a project county or project-level supply volume, so the indexer omits those absent claims instead of rendering blank values or inventing units.

## EPA SDWIS data fields

| Field | Description |
|-------|-------------|
| `pwsid` | Public water system ID |
| `pws_name` | System name |
| `pws_type_code` | CWS (community), NTNCWS, TNCWS |
| `primacy_agency_code` | State/EPA region with primacy |
| `population_served_count` | Estimated population served |
| `pws_activity_code` | Active, inactive, etc. |
| `source_water_type` | GW (groundwater), SW (surface water), GU |
| `violation_flag` | Has active or recent violations |

## AI Search document structure

**TWDB water project:**
```json
{
  "id": "twdb_project_harris_tx_1234",
  "content": "Water supply project: Lake Houston Aquifer Storage & Recovery. Sponsor: City of Houston. Harris County, Region H. Surface water reuse strategy. Volume 2030: 50,000 ac-ft/yr. Cost 2030: $340,000,000.",
  "content_vector": [0.015, -0.029, ...],
  "source": "TWDB_2027_State_Water_Plan",
  "domain": "water",
  "document_type": "water_plan_project",
  "state": "TX",
  "county": "Harris"
}
```

**EPA water system:**
```json
{
  "id": "sdwis_TX0010001",
  "content": "Community water system: City of Houston PWS (TX0010001). Population served: 2,100,000. Source: surface water. Status: active. No current health-based violations.",
  "content_vector": [0.011, -0.022, ...],
  "source": "EPA_SDWIS",
  "domain": "water",
  "document_type": "water_system_record",
  "state": "TX",
  "county": "Harris"
}
```

## Volume

- TWDB: ~3,000 water supply projects across 16 regional planning areas (A–P)
- EPA SDWIS: ~3,500 Texas community water systems

Monthly refresh updates both datasets in full, as TWDB publishes workbook revisions and SDWIS status changes. Logs record delivery format, byte counts, sheet and record counts, and Blob manifest operations without serializing workbook rows, credentials, or provider response bodies. OpenLineage emits the DAG and task lifecycle to Datadog Data Jobs Monitoring, while Blob upload spans expose storage latency and payload size for operational diagnosis.
