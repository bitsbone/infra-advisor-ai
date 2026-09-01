"""FEMA disaster declarations — daily refresh.

Ported from services/ingestion/dags/fema_refresh.py. Pagination and
narrative-building logic is unchanged; the XCom manifest hand-off is
replaced by direct blob reads/writes (see shared/blob_io.py).
"""

import logging

import requests

from shared.blob_io import PREPARED_CONTAINER, RAW_CONTAINER, write_json_records, write_parquet_records

logger = logging.getLogger(__name__)

FEMA_API_URL = "https://www.fema.gov/api/open/v2/DisasterDeclarationsSummaries"
FILTER_DATE_FROM = "2010-01-01T00:00:00.000Z"
PAGE_SIZE = 1000


def _fetch_all_records() -> list[dict]:
    all_records: list[dict] = []
    skip = 0
    while True:
        params = {
            "$filter": f"declarationDate ge '{FILTER_DATE_FROM}'",
            "$format": "json",
            "$top": PAGE_SIZE,
            "$skip": skip,
            "$orderby": "declarationDate asc",
        }
        resp = requests.get(FEMA_API_URL, params=params, timeout=60)
        resp.raise_for_status()
        records = resp.json().get("DisasterDeclarationsSummaries", [])
        logger.info("Fetched %d FEMA records at skip=%d", len(records), skip)
        all_records.extend(records)
        if len(records) < PAGE_SIZE:
            break
        skip += PAGE_SIZE
    return all_records


def _build_narrative(rec: dict) -> tuple[str, str]:
    """Returns (doc_id_prefix, narrative)."""
    disaster_number = rec.get("disasterNumber", "UNKNOWN")
    fips = rec.get("fipsStateCode", "") + rec.get("fipsCountyCode", "")
    narrative = (
        f"FEMA Disaster {disaster_number} — {rec.get('declarationTitle', '')}. "
        f"State: {rec.get('stateCode', '')}. Designated area: {rec.get('designatedArea', '')} (FIPS: {fips}). "
        f"Declaration type: {rec.get('declarationType', '')}. Incident type: {rec.get('incidentType', '')}. "
        f"Declaration date: {rec.get('declarationDate', '')}. "
        f"Incident period: {rec.get('incidentBeginDate', '')} to {rec.get('incidentEndDate', '')}. "
        f"Closeout date: {rec.get('disasterCloseoutDate', '')}. "
        f"Public Assistance declared: {rec.get('paDeclarationString', '')}. "
        f"Hazard Mitigation declared: {rec.get('hmDeclarationString', '')}."
    )
    return f"fema_{disaster_number}", narrative


def fetch_and_store(run_id: str) -> dict:
    records = _fetch_all_records()
    if not records:
        return {"prepared_blob_path": None, "record_count": 0}

    write_parquet_records(RAW_CONTAINER, f"fema/{run_id}.parquet", records)

    prepared = []
    for rec in records:
        doc_id_prefix, narrative = _build_narrative(rec)
        prepared.append({
            "doc_id_prefix": doc_id_prefix,
            "narrative": narrative,
            "domain": "environmental",
            "document_type": "disaster_declaration",
            "source": "OpenFEMA",
            "source_url": FEMA_API_URL,
        })

    prepared_blob_path = f"fema/{run_id}.json"
    write_json_records(PREPARED_CONTAINER, prepared_blob_path, prepared)
    return {"prepared_blob_path": prepared_blob_path, "record_count": len(records)}
