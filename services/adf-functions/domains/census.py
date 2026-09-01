"""Census population growth + building permits market intelligence — monthly.

Ported from services/ingestion/dags/census_market_intelligence_refresh.py.
This is the fan-in domain: fetch_population and fetch_permits run
independently (ADF runs them as parallel activities), then
build_prepared_records joins them by (state_fips, county_fips) before
handing off to the shared chunk/embed/upsert step. Each county produces
one short narrative — under the shared chunker's 512-token window this
naturally stays a single chunk, matching the original DAG's no-sub-chunk
behavior without needing a special case.
"""

import logging

import requests

from shared.blob_io import PREPARED_CONTAINER, RAW_CONTAINER, write_json_records, write_parquet_records

logger = logging.getLogger(__name__)

CENSUS_POP_BASE = "https://api.census.gov/data/2023/pep/population"
CENSUS_PERMITS_BASE = "https://api.census.gov/data/timeseries/eits/bps"

# High-growth states: TX=48, FL=12, AZ=04, CO=08, NC=37, GA=13, TN=47, NV=32
HIGH_GROWTH_STATES = ["48", "12", "04", "08", "37", "13", "47", "32"]

STATE_FIPS_TO_NAME = {
    "48": "Texas", "12": "Florida", "04": "Arizona", "08": "Colorado",
    "37": "North Carolina", "13": "Georgia", "47": "Tennessee", "32": "Nevada",
}


def _demand_indicator(growth_pct: float) -> str:
    if growth_pct > 5.0:
        return "high"
    if growth_pct >= 2.0:
        return "medium"
    return "low"


def fetch_population(run_id: str) -> dict:
    """ADF-parallel activity 1. Returns {"blob_path": ..., "record_count": ...}."""
    all_counties = []
    for state_fips in HIGH_GROWTH_STATES:
        params = {"get": "NAME,POP_2020,POP_2021,POP_2022,POP_2023", "for": "county:*", "in": f"state:{state_fips}"}
        try:
            resp = requests.get(CENSUS_POP_BASE, params=params, timeout=60)
            resp.raise_for_status()
            rows = resp.json()
        except Exception as exc:
            logger.warning("Census population fetch failed for state %s: %s", state_fips, exc)
            continue
        if not rows or len(rows) < 2:
            logger.warning("No population data returned for state %s", state_fips)
            continue
        headers = rows[0]
        for row in rows[1:]:
            record = dict(zip(headers, row))
            record["_state_fips"] = state_fips
            all_counties.append(record)
        logger.info("Fetched %d counties for state %s", len(rows) - 1, state_fips)

    logger.info("Total county population records fetched: %d", len(all_counties))
    blob_path = f"census/population/{run_id}.json"
    write_json_records(RAW_CONTAINER, blob_path, all_counties)
    return {"blob_path": blob_path, "record_count": len(all_counties)}


def fetch_permits(run_id: str) -> dict:
    """ADF-parallel activity 2. Returns {"blob_path": ..., "record_count": ...}."""
    all_permits = []
    for state_fips in HIGH_GROWTH_STATES:
        params = {"get": "cell_value,time_slot_id,category_code", "for": "county:*", "in": f"state:{state_fips}"}
        try:
            resp = requests.get(CENSUS_PERMITS_BASE, params=params, timeout=60)
            resp.raise_for_status()
            rows = resp.json()
        except Exception as exc:
            logger.warning("Census permits fetch failed for state %s: %s", state_fips, exc)
            continue
        if not rows or len(rows) < 2:
            logger.warning("No permit data returned for state %s", state_fips)
            continue
        headers = rows[0]
        for row in rows[1:]:
            record = dict(zip(headers, row))
            record["_state_fips"] = state_fips
            all_permits.append(record)
        logger.info("Fetched %d permit records for state %s", len(rows) - 1, state_fips)

    logger.info("Total building permit records fetched: %d", len(all_permits))
    blob_path = f"census/permits/{run_id}.json"
    write_json_records(RAW_CONTAINER, blob_path, all_permits)
    return {"blob_path": blob_path, "record_count": len(all_permits)}


def build_prepared_records(run_id: str, population_blob_path: str, permits_blob_path: str) -> dict:
    """ADF activity 3 — dependsOn BOTH fetch_population and fetch_permits.

    Joins population + permits, writes raw Parquet archives for both, and
    writes the prepared-records blob the shared index step consumes.
    """
    from shared.blob_io import read_json_records

    population_data = read_json_records(RAW_CONTAINER, population_blob_path)
    permit_data = read_json_records(RAW_CONTAINER, permits_blob_path)

    if population_data:
        write_parquet_records(RAW_CONTAINER, f"census/population/{run_id}.parquet", population_data)
    if permit_data:
        write_parquet_records(RAW_CONTAINER, f"census/permits/{run_id}.parquet", permit_data)

    if not population_data:
        return {"prepared_blob_path": None, "record_count": 0}

    permit_lookup: dict[tuple[str, str], int] = {}
    for rec in permit_data:
        state = rec.get("_state_fips", "")
        county = rec.get("county", "")
        try:
            count = int(rec.get("cell_value") or 0)
        except (TypeError, ValueError):
            count = 0
        key = (state, county)
        permit_lookup[key] = permit_lookup.get(key, 0) + count

    prepared = []
    for rec in population_data:
        county_name = rec.get("NAME", "Unknown County")
        state_fips = rec.get("_state_fips", "")
        county_fips = rec.get("county", "")
        state_name = STATE_FIPS_TO_NAME.get(state_fips, f"State {state_fips}")

        try:
            pop_2020 = int(rec.get("POP_2020") or 0)
            pop_2023 = int(rec.get("POP_2023") or 0)
        except (TypeError, ValueError):
            pop_2020 = 0
            pop_2023 = 0
        growth_pct = ((pop_2023 - pop_2020) / pop_2020 * 100) if pop_2020 > 0 else 0.0
        total_permits = permit_lookup.get((state_fips, county_fips), 0)
        demand = _demand_indicator(growth_pct)

        narrative = (
            f"Market intelligence: {county_name}, {state_name}. "
            f"Population 2023: {pop_2023:,} (growth since 2020: {growth_pct:.1f}%). "
            f"Building permits issued: {total_permits:,} (latest available year). "
            f"Infrastructure demand indicator: {demand} based on growth rate."
        )
        safe_county = county_name.replace(" ", "_").replace(",", "").replace("/", "-")
        prepared.append({
            "doc_id_prefix": f"census_{state_fips}_{county_fips}_{safe_county}",
            "narrative": narrative,
            "domain": "business_development",
            "document_type": "market_intelligence",
            "source": "US Census Bureau",
            "source_url": CENSUS_POP_BASE,
        })

    prepared_blob_path = f"census/{run_id}.json"
    write_json_records(PREPARED_CONTAINER, prepared_blob_path, prepared)
    return {"prepared_blob_path": prepared_blob_path, "record_count": len(prepared)}
