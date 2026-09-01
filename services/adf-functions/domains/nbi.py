"""NBI (National Bridge Inventory) Texas bridges — weekly refresh.

Ported from services/ingestion/dags/nbi_refresh.py. The original DAG used
a bespoke 500-character chunker; that's now replaced by the shared
tiktoken 512-token/64-overlap chunker (shared/chunking.py via
shared/search_upsert.py) as part of the deliberate chunking
standardization — expect different chunk boundaries/doc counts than the
old Airflow pipeline produced for this domain.
"""

import logging

import requests

from shared.blob_io import PREPARED_CONTAINER, RAW_CONTAINER, write_json_records, write_parquet_records

logger = logging.getLogger(__name__)

NBI_ARCGIS_URL = (
    "https://services.arcgis.com/xOi1kZaI0eWDREZv/arcgis/rest/services"
    "/National_Bridge_Inventory/FeatureServer/0/query"
)
NBI_FIELDS = ",".join([
    "STRUCTURE_NUMBER_008", "FACILITY_CARRIED_007", "LOCATION_009", "COUNTY_CODE_003",
    "STATE_CODE_001", "ADT_029", "YEAR_ADT_030", "DECK_COND_058", "SUPERSTRUCTURE_COND_059",
    "SUBSTRUCTURE_COND_060", "STRUCTURALLY_DEFICIENT", "SUFFICIENCY_RATING",
    "INSPECT_DATE_090", "YEAR_BUILT_027", "LAT_016", "LONG_017",
])
STATE_CODE_TX = "48"
PAGE_SIZE = 2000

CONDITION_LABELS = {
    "9": "excellent", "8": "very good", "7": "good",
    "6": "satisfactory", "5": "fair", "4": "poor",
    "3": "serious", "2": "critical", "1": "imminent failure", "0": "failed",
}


def _fetch_all_features() -> list[dict]:
    all_features: list[dict] = []
    offset = 0
    while True:
        params = {
            "where": f"STATE_CODE_001='{STATE_CODE_TX}' AND SUFFICIENCY_RATING IS NOT NULL",
            "outFields": NBI_FIELDS,
            "resultOffset": offset,
            "resultRecordCount": PAGE_SIZE,
            "f": "json",
        }
        resp = requests.get(NBI_ARCGIS_URL, params=params, timeout=60)
        resp.raise_for_status()
        features = resp.json().get("features", [])
        logger.info("Fetched %d NBI records at offset %d", len(features), offset)
        all_features.extend(features)
        if len(features) < PAGE_SIZE:
            break
        offset += PAGE_SIZE
    return [f["attributes"] for f in all_features]


def _build_narrative(rec: dict) -> tuple[str, str]:
    struct_num = str(rec.get("STRUCTURE_NUMBER_008", "UNKNOWN")).strip()
    deck = CONDITION_LABELS.get(str(rec.get("DECK_COND_058", "")), "unknown")
    superstr = CONDITION_LABELS.get(str(rec.get("SUPERSTRUCTURE_COND_059", "")), "unknown")
    substr = CONDITION_LABELS.get(str(rec.get("SUBSTRUCTURE_COND_060", "")), "unknown")
    sd_flag = "Yes" if str(rec.get("STRUCTURALLY_DEFICIENT", "")) == "1" else "No"
    narrative = (
        f"Bridge Structure {struct_num} — Texas (State Code 48). "
        f"Facility carried: {rec.get('FACILITY_CARRIED_007', '')}. Location: {rec.get('LOCATION_009', '')}. "
        f"County code: {rec.get('COUNTY_CODE_003', '')}. Average daily traffic: {rec.get('ADT_029', '')}. "
        f"Deck condition: {deck}. Superstructure condition: {superstr}. "
        f"Substructure condition: {substr}. Structurally deficient: {sd_flag}. "
        f"Sufficiency rating: {rec.get('SUFFICIENCY_RATING', 'N/A')}. Last inspection: {rec.get('INSPECT_DATE_090', '')}. "
        f"Year built: {rec.get('YEAR_BUILT_027', '')}. Coordinates: {rec.get('LAT_016', '')}, {rec.get('LONG_017', '')}."
    )
    safe_struct = struct_num.replace(" ", "_").replace("/", "-")
    return f"nbi_{safe_struct}", narrative


def fetch_and_store(run_id: str) -> dict:
    records = _fetch_all_features()
    if not records:
        return {"prepared_blob_path": None, "record_count": 0}

    write_parquet_records(RAW_CONTAINER, f"nbi/texas/{run_id}.parquet", records)

    prepared = []
    for rec in records:
        doc_id_prefix, narrative = _build_narrative(rec)
        prepared.append({
            "doc_id_prefix": doc_id_prefix,
            "narrative": narrative,
            "domain": "transportation",
            "document_type": "asset_record",
            "source": "FHWA_NBI",
            "source_url": NBI_ARCGIS_URL,
        })

    prepared_blob_path = f"nbi/texas/{run_id}.json"
    write_json_records(PREPARED_CONTAINER, prepared_blob_path, prepared)
    return {"prepared_blob_path": prepared_blob_path, "record_count": len(records)}
