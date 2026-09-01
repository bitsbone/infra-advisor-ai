"""Supplemental real-data report generation — weekly, idempotency-gated.

Ported from services/ingestion/scripts/fetch_public_docs.py. Four
independent report generators (FEMA disaster profiles, FEMA Hazard
Mitigation projects, EIA state energy profiles, NBI county bridge
summaries — the latter via bridgeapi.azurewebsites.net, a THIRD, distinct
NBI source from nbi_refresh's ArcGIS FeatureServer — do not conflate the
two), each isolated in its own try/except so one source failing never
blocks the others. Unlike the other domains, this writes no raw Parquet
archive (the original script didn't either) — output goes straight to
the prepared-records blob for the shared index step.

The idempotency gate (skip if the index already has >= 200 non-synthetic
docs) is checked here too for defense-in-depth, even though ADF's
pipeline design puts the primary gate in a native Web Activity +
If-Condition ahead of this function.
"""

import logging
import os
from collections import defaultdict
from datetime import datetime, timezone

import httpx

from shared.blob_io import PREPARED_CONTAINER, write_json_records

logger = logging.getLogger(__name__)

EXISTING_THRESHOLD = 200

STATE_FIPS = {"TX": "48", "CA": "06", "FL": "12", "LA": "22", "OK": "40", "AZ": "04"}
STATE_NAMES = {"TX": "Texas", "CA": "California", "FL": "Florida", "LA": "Louisiana", "OK": "Oklahoma", "AZ": "Arizona"}
TARGET_STATES = ["TX", "LA", "FL", "OK", "AZ", "CA"]

FEMA_DISASTER_URL = "https://www.fema.gov/api/open/v2/DisasterDeclarationsSummaries"
FEMA_HM_URL = "https://www.fema.gov/api/open/v2/HazardMitigationGrantProgramProjectActivities"
EIA_RETAIL_URL = "https://api.eia.gov/v2/electricity/retail-sales/data"
NBI_BRIDGE_URL = "https://bridgeapi.azurewebsites.net/api/bridges"


def _count_existing_real_docs() -> int:
    from shared.search_upsert import count_indexed_documents
    return count_indexed_documents("source ne 'synthetic'")


def fetch_fema_disaster_profiles(states: list[str]) -> list[dict]:
    docs = []
    year_now = datetime.now(timezone.utc).year
    for state in states:
        state_name = STATE_NAMES.get(state, state)
        params = {"$format": "json", "$top": 1000, "$filter": f"state eq '{state}'"}
        try:
            with httpx.Client(timeout=30) as client:
                resp = client.get(FEMA_DISASTER_URL, params=params)
                resp.raise_for_status()
                data = resp.json()
        except Exception as exc:
            logger.warning("FEMA disaster fetch failed for %s: %s", state, exc)
            continue

        records = data.get("DisasterDeclarationsSummaries", [])
        if not records:
            logger.info("No FEMA disaster records returned for %s", state)
            continue
        logger.info("Fetched %d FEMA disaster records for %s", len(records), state)

        by_type: dict[str, dict] = defaultdict(lambda: {"count": 0, "counties": set()})
        all_years = []
        recent_disasters = []
        for rec in records:
            itype = rec.get("incidentType", "Unknown")
            by_type[itype]["count"] += 1
            county = rec.get("designatedArea", "")
            if county:
                by_type[itype]["counties"].add(county)
            decl_date = rec.get("declarationDate", "")
            if decl_date:
                try:
                    yr = int(decl_date[:4])
                    all_years.append(yr)
                    if yr >= year_now - 5:
                        recent_disasters.append({
                            "title": rec.get("declarationTitle", ""),
                            "date": decl_date[:10],
                            "counties": rec.get("designatedArea", ""),
                        })
                except (ValueError, IndexError):
                    pass

        earliest = min(all_years) if all_years else "unknown"
        latest = max(all_years) if all_years else "unknown"
        total = len(records)

        breakdown_lines = [
            f"- {itype}: {info['count']} declarations, {len(info['counties'])} counties affected"
            for itype, info in sorted(by_type.items(), key=lambda x: -x[1]["count"])
        ]
        recent_lines = [
            f"- {d['title']}: declared {d['date']}, counties: {d['counties']}"
            for d in sorted(recent_disasters, key=lambda x: x["date"], reverse=True)[:5]
        ]
        top_names = [t[0] for t in sorted(by_type.items(), key=lambda x: -x[1]["count"])[:3]]
        implications = (
            f"{state_name} faces recurring infrastructure risk from "
            f"{', '.join(top_names)}. These events drive demand for hardened "
            f"transportation and utility infrastructure, FEMA Public Assistance-"
            f"eligible repairs, and Hazard Mitigation Grant Program investments "
            f"targeting flood control, bridge scour protection, and grid resilience. "
            f"Frequency trends suggest increasing exposure to climate-driven events "
            f"requiring proactive capital planning."
        )
        content = (
            f"# FEMA Disaster Profile — {state_name}\n\n"
            f"## Summary\nTotal disaster declarations: {total}\n"
            f"States covered: {state}\nPeriod: {earliest} – {latest}\n\n"
            f"## Disaster Type Breakdown\n" + "\n".join(breakdown_lines)
            + "\n\n## Notable Recent Disasters (last 5 years)\n"
            + ("\n".join(recent_lines) if recent_lines else "- No recent declarations in dataset")
            + f"\n\n## Infrastructure Implications\n{implications}\n"
        )
        docs.append({
            "id": f"fema_disaster_{state.lower()}_{year_now}",
            "content": content,
            "source": "OpenFEMA_Disaster_Declarations",
            "document_type": "disaster_profile",
            "domain": "disaster",
            "source_url": FEMA_DISASTER_URL,
        })
    return docs


def fetch_fema_hm_projects(states: list[str]) -> list[dict]:
    docs = []
    year_now = datetime.now(timezone.utc).year
    for state in states:
        state_name = STATE_NAMES.get(state, state)
        params = {"$format": "json", "$top": 200, "$filter": f"state eq '{state}'"}
        try:
            with httpx.Client(timeout=30) as client:
                resp = client.get(FEMA_HM_URL, params=params)
                resp.raise_for_status()
                data = resp.json()
        except Exception as exc:
            logger.warning("FEMA HM fetch failed for %s: %s", state, exc)
            continue

        records = data.get("HazardMitigationGrantProgramProjectActivities", [])
        if not records:
            logger.info("No FEMA HM records returned for %s", state)
            continue
        logger.info("Fetched %d FEMA HM records for %s", len(records), state)

        by_type: dict[str, dict] = defaultdict(lambda: {"count": 0, "cost": 0.0})
        for rec in records:
            ptype = rec.get("projectType", "Unknown") or "Unknown"
            by_type[ptype]["count"] += 1
            try:
                by_type[ptype]["cost"] += float(rec.get("federalShareObligated", "") or "0")
            except (ValueError, TypeError):
                pass

        total_projects = len(records)
        total_cost = sum(v["cost"] for v in by_type.values())
        breakdown_lines = [
            f"- {ptype}: {info['count']} projects, ${info['cost'] / 1_000_000:.1f}M federal share obligated"
            for ptype, info in sorted(by_type.items(), key=lambda x: -x[1]["count"])
        ]
        content = (
            f"# FEMA Hazard Mitigation Grant Program — {state_name}\n\n"
            f"## Summary\nTotal HM project activities: {total_projects}\n"
            f"Total federal share obligated: ${total_cost / 1_000_000:.1f}M\nState: {state}\n\n"
            f"## Project Type Breakdown\n" + "\n".join(breakdown_lines)
            + "\n\n## Strategic Context\n"
            f"Hazard Mitigation Grant Program investments in {state_name} reflect "
            f"the state's disaster risk profile. The project mix indicates priority "
            f"areas for resilience investment including flood mitigation, structural "
            f"hardening, and utility protection. These grant categories align with "
            f"FEMA BRIC competitive program priorities and inform benefit-cost analysis "
            f"strategies for future grant applications.\n"
        )
        docs.append({
            "id": f"fema_hm_{state.lower()}_{year_now}",
            "content": content,
            "source": "OpenFEMA_Hazard_Mitigation",
            "document_type": "hazard_mitigation_report",
            "domain": "disaster",
            "source_url": FEMA_HM_URL,
        })
    return docs


def fetch_eia_state_profiles(states: list[str]) -> list[dict]:
    eia_key = os.environ.get("EIA_API_KEY")
    if not eia_key:
        logger.warning("EIA_API_KEY not set — skipping EIA state energy profiles.")
        return []

    docs = []
    year_now = datetime.now(timezone.utc).year
    for state in states:
        state_name = STATE_NAMES.get(state, state)
        params = {
            "api_key": eia_key, "frequency": "annual", "facets[stateid][]": state,
            "data[0]": "price", "data[1]": "sales",
            "sort[0][column]": "period", "sort[0][direction]": "desc", "length": 10,
        }
        try:
            with httpx.Client(timeout=30) as client:
                resp = client.get(EIA_RETAIL_URL, params=params)
                resp.raise_for_status()
                data = resp.json()
        except Exception as exc:
            logger.warning("EIA fetch failed for %s: %s", state, exc)
            continue

        rows = data.get("response", {}).get("data", [])
        if not rows:
            logger.info("No EIA data returned for %s", state)
            continue
        logger.info("Fetched %d EIA rows for %s", len(rows), state)

        by_year: dict[str, list] = defaultdict(list)
        for row in rows:
            by_year[row.get("period", "")].append(row)

        year_lines = []
        for period in sorted(by_year.keys(), reverse=True):
            period_rows = by_year[period]
            prices = [r.get("price") for r in period_rows if r.get("price") is not None]
            sales = [r.get("sales") for r in period_rows if r.get("sales") is not None]
            avg_price = sum(float(p) for p in prices) / len(prices) if prices else None
            total_sales = sum(float(s) for s in sales) if sales else None
            price_str = f"{avg_price:.2f} cents/kWh" if avg_price is not None else "N/A"
            sales_str = f"{total_sales:,.0f} MWh" if total_sales is not None else "N/A"
            year_lines.append(f"- {period}: average retail price {price_str}, total sales {sales_str}")

        content = (
            f"# EIA State Energy Profile — {state_name}\n\n"
            f"## Summary\nState: {state}\n"
            f"Source: U.S. Energy Information Administration (EIA) Retail Electricity Sales\n"
            f"Data coverage: last 10 annual periods\n\n"
            f"## Annual Retail Electricity Sales and Prices\n" + "\n".join(year_lines)
            + "\n\n## Infrastructure Context\n"
            f"Retail electricity price and consumption trends in {state_name} reflect "
            f"the state's generation mix, transmission infrastructure condition, and "
            f"demand growth driven by population and industrial activity. Price variability "
            f"signals grid stress periods and the value of demand-side management, "
            f"distributed generation, and resilience investments. These data inform "
            f"energy infrastructure planning, IIJA grid resilience grant applications, "
            f"and utility rate analysis for municipal clients.\n"
        )
        docs.append({
            "id": f"eia_state_{state.lower()}_{year_now}",
            "content": content,
            "source": "EIA_Electricity_Retail",
            "document_type": "state_energy_profile",
            "domain": "energy",
            "source_url": EIA_RETAIL_URL,
        })
    return docs


def fetch_nbi_county_summaries(states: list[str]) -> list[dict]:
    docs = []
    year_now = datetime.now(timezone.utc).year
    for state in states:
        fips = STATE_FIPS.get(state)
        state_name = STATE_NAMES.get(state, state)
        if not fips:
            logger.warning("No FIPS code for state %s — skipping NBI fetch", state)
            continue

        params = {"state": fips, "limit": 500}
        try:
            with httpx.Client(timeout=30) as client:
                resp = client.get(NBI_BRIDGE_URL, params=params)
                resp.raise_for_status()
                bridges = resp.json()
        except Exception as exc:
            logger.warning("NBI fetch failed for %s (FIPS %s): %s", state, fips, exc)
            continue

        if not bridges or not isinstance(bridges, list):
            logger.info("No NBI bridge records returned for %s", state)
            continue
        logger.info("Fetched %d NBI bridge records for %s", len(bridges), state)

        total_bridges = len(bridges)
        by_county: dict[str, list] = defaultdict(list)
        for bridge in bridges:
            by_county[str(bridge.get("COUNTY_CODE_003", "Unknown"))].append(bridge)

        sd_count = sum(1 for b in bridges if str(b.get("STRUCTURALLY_DEFICIENT", "")) == "1")
        pre_1970 = sum(1 for b in bridges if b.get("YEAR_BUILT_027") and int(b.get("YEAR_BUILT_027", 9999)) < 1970)
        scour_critical = sum(1 for b in bridges if str(b.get("SCOUR_CRITICAL_113", "")) in ("U", "3", "2"))
        sd_pct = (sd_count / total_bridges * 100) if total_bridges else 0

        county_stats = []
        for county, cbridges in by_county.items():
            c_total = len(cbridges)
            c_sd = sum(1 for b in cbridges if str(b.get("STRUCTURALLY_DEFICIENT", "")) == "1")
            county_stats.append((county, c_total, c_sd, c_sd / c_total if c_total else 0))
        county_stats.sort(key=lambda x: -x[3])
        top5_lines = [
            f"- County {county}: {c_sd}/{c_total} structurally deficient ({c_rate * 100:.1f}% deficiency rate)"
            for county, c_total, c_sd, c_rate in county_stats[:5]
        ]

        content = (
            f"# NBI Bridge Inventory Summary — {state_name}\n\n"
            f"## Overall Statistics\nTotal bridges surveyed: {total_bridges}\n"
            f"Structurally deficient: {sd_count} ({sd_pct:.1f}%)\n"
            f"Built before 1970: {pre_1970} ({pre_1970 / total_bridges * 100:.1f}% of inventory)\n"
            f"Scour-critical: {scour_critical}\nCounties represented: {len(by_county)}\n\n"
            f"## Top 5 Counties by Structural Deficiency Rate\n"
            + "\n".join(top5_lines if top5_lines else ["- No county data available"])
            + "\n\n## Infrastructure Implications\n"
            f"The bridge inventory in {state_name} shows {sd_pct:.1f}% structural deficiency, "
            f"with {pre_1970} structures built before modern AASHTO LRFD design standards. "
            f"Scour-critical designations at {scour_critical} bridges indicate flood vulnerability "
            f"requiring HEC-18 assessments and countermeasure investment. FHWA Bridge Formula "
            f"Program funds under IIJA provide a priority funding pathway for rehabilitation "
            f"of structurally deficient bridges, particularly in counties with deficiency rates "
            f"exceeding the national average of approximately 7%.\n"
        )
        docs.append({
            "id": f"nbi_county_summary_{state.lower()}_{year_now}",
            "content": content,
            "source": "FHWA_NBI",
            "document_type": "bridge_inventory_summary",
            "domain": "transportation",
            "source_url": NBI_BRIDGE_URL,
        })
    return docs


def fetch_and_prepare(run_id: str) -> dict:
    existing_count = _count_existing_real_docs()
    logger.info("Found %d existing non-synthetic documents in index.", existing_count)
    if existing_count >= EXISTING_THRESHOLD:
        logger.info("Index already has %d real documents (>= threshold %d). Skipping.", existing_count, EXISTING_THRESHOLD)
        return {"prepared_blob_path": None, "record_count": 0, "skipped": True}

    raw_docs: list[dict] = []
    for fetcher in (fetch_fema_disaster_profiles, fetch_fema_hm_projects, fetch_eia_state_profiles, fetch_nbi_county_summaries):
        try:
            raw_docs.extend(fetcher(TARGET_STATES))
        except Exception as exc:
            logger.error("%s failed: %s", fetcher.__name__, exc, exc_info=True)

    prepared = [
        {
            "doc_id_prefix": doc["id"],
            "narrative": doc["content"],
            "domain": doc["domain"],
            "document_type": doc["document_type"],
            "source": doc["source"],
            "source_url": doc.get("source_url"),
        }
        for doc in raw_docs
    ]
    prepared_blob_path = f"public-docs/{run_id}.json"
    write_json_records(PREPARED_CONTAINER, prepared_blob_path, prepared)
    return {"prepared_blob_path": prepared_blob_path, "record_count": len(prepared), "skipped": False}
