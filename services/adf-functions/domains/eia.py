"""EIA electricity generation/capacity — weekly refresh, southeastern states.

Ported from services/ingestion/dags/eia_refresh.py. The two-level
(state x offset) pagination stays inside this one function rather than
being modeled as an ADF ForEach, per the migration design.
"""

import logging
import os

import requests

from shared.blob_io import PREPARED_CONTAINER, RAW_CONTAINER, write_json_records, write_parquet_records

logger = logging.getLogger(__name__)

EIA_API_URL = "https://api.eia.gov/v2/electricity/electric-power-operational-data/data/"
SOUTHEASTERN_STATES = ["FL", "GA", "AL", "MS", "LA", "TX", "AR", "TN", "SC", "NC", "VA"]
PAGE_SIZE = 5000


def _fetch_all_records() -> list[dict]:
    eia_api_key = os.environ["EIA_API_KEY"]
    all_records: list[dict] = []
    for state in SOUTHEASTERN_STATES:
        offset = 0
        while True:
            params = {
                "api_key": eia_api_key,
                "frequency": "annual",
                "data[]": ["generation", "capacity"],
                "facets[location][]": state,
                "sort[0][column]": "period",
                "sort[0][direction]": "desc",
                "offset": offset,
                "length": PAGE_SIZE,
            }
            resp = requests.get(EIA_API_URL, params=params, timeout=60)
            resp.raise_for_status()
            data = resp.json()
            page_records = data.get("response", {}).get("data", [])
            logger.info("EIA: fetched %d records for state=%s offset=%d", len(page_records), state, offset)
            for rec in page_records:
                rec["state_code"] = state
            all_records.extend(page_records)
            total = data.get("response", {}).get("total", 0)
            offset += PAGE_SIZE
            if offset >= total or len(page_records) == 0:
                break
    return all_records


def _build_narrative(idx: int, rec: dict) -> tuple[str, str]:
    period = rec.get("period", "")
    state = rec.get("state_code", rec.get("location", ""))
    sector = rec.get("sectorDescription", rec.get("sector-name", ""))
    fuel_type = rec.get("fuelTypeDescription", rec.get("fueltypeid", ""))
    generation = rec.get("generation", "")
    generation_units = rec.get("generation-units", "thousand megawatthours")
    capacity = rec.get("capacity", "")
    capacity_units = rec.get("capacity-units", "gigawatts")
    narrative = (
        f"EIA Electric Power Data — State: {state}, Period: {period}. "
        f"Sector: {sector}. Fuel type: {fuel_type}. "
        f"Net generation: {generation} {generation_units}. "
        f"Capacity: {capacity} {capacity_units}. "
        f"Source: EIA Electric Power Operational Data API."
    )
    doc_id_prefix = f"eia_{state}_{period}_{idx}".replace(" ", "_")
    return doc_id_prefix, narrative


def fetch_and_store(run_id: str) -> dict:
    records = _fetch_all_records()
    if not records:
        return {"prepared_blob_path": None, "record_count": 0}

    write_parquet_records(RAW_CONTAINER, f"eia/{run_id}.parquet", records)

    prepared = []
    for idx, rec in enumerate(records):
        doc_id_prefix, narrative = _build_narrative(idx, rec)
        prepared.append({
            "doc_id_prefix": doc_id_prefix,
            "narrative": narrative,
            "domain": "energy",
            "document_type": "energy_record",
            "source": "EIA",
            "source_url": EIA_API_URL,
        })

    prepared_blob_path = f"eia/{run_id}.json"
    write_json_records(PREPARED_CONTAINER, prepared_blob_path, prepared)
    return {"prepared_blob_path": prepared_blob_path, "record_count": len(records)}
