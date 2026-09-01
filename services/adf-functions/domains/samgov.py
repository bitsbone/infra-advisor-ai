"""USASpending.gov infrastructure contract awards — weekly refresh.

Ported from services/ingestion/dags/samgov_awards_refresh.py (misleadingly
named in the original — it queries USASpending.gov, not SAM.gov). The
original DAG chunked with no token overlap; replaced by the shared
512-token/64-overlap chunker as part of the chunking standardization —
expect different chunk boundaries/doc counts than the old pipeline.

Per-page fetch failures are logged and skipped rather than aborting the
whole run, matching the original DAG's error tolerance.
"""

import logging
from datetime import datetime, timedelta, timezone

import requests

from shared.blob_io import PREPARED_CONTAINER, RAW_CONTAINER, write_json_records, write_parquet_records

logger = logging.getLogger(__name__)

USASPENDING_URL = "https://api.usaspending.gov/api/v2/search/spending_by_award/"
MIN_AWARD_USD = 500_000
PAGE_SIZE = 100
_NAICS_PREFIXES = ["2371", "2373", "2372", "2379"]

AWARD_TYPE_LABELS = {
    "A": "BPA Call",
    "B": "Purchase Order",
    "C": "Delivery Order",
    "D": "Definitive Contract",
}


def _fetch_all_awards() -> list[dict]:
    today = datetime.now(timezone.utc).date()
    date_from = (today - timedelta(days=365)).isoformat()
    date_to = today.isoformat()

    all_awards: list[dict] = []
    for naics_prefix in _NAICS_PREFIXES:
        page = 1
        while True:
            body = {
                "filters": {
                    "time_period": [{"start_date": date_from, "end_date": date_to}],
                    "award_type_codes": ["A", "B", "C", "D"],
                    "naics_codes": [naics_prefix],
                },
                "fields": [
                    "Award ID", "Recipient Name", "Award Amount", "Total Outlays",
                    "Description", "Start Date", "End Date",
                    "Awarding Agency", "Awarding Sub Agency", "Contract Award Type",
                    "Place of Performance State Code", "Place of Performance City Name",
                    "naics_code", "naics_description",
                ],
                "page": page,
                "limit": PAGE_SIZE,
                "sort": "Award Amount",
                "order": "desc",
            }
            try:
                resp = requests.post(USASPENDING_URL, json=body, timeout=60)
                resp.raise_for_status()
                data = resp.json()
            except Exception as exc:
                logger.warning("USASpending fetch failed for NAICS prefix %s page %d: %s", naics_prefix, page, exc)
                break

            results = data.get("results", [])
            logger.info("NAICS %s page %d: %d records", naics_prefix, page, len(results))
            all_awards.extend(results)
            if len(results) < PAGE_SIZE:
                break
            page += 1

    filtered = []
    for award in all_awards:
        amount = award.get("Award Amount") or award.get("Total Outlays") or 0
        try:
            amount = float(amount)
        except (TypeError, ValueError):
            amount = 0.0
        if amount >= MIN_AWARD_USD:
            award["_amount_float"] = amount
            filtered.append(award)
    logger.info("Total awards after filtering (>= $%d): %d", MIN_AWARD_USD, len(filtered))
    return filtered


def _build_narrative(award: dict) -> tuple[str, str, str]:
    """Returns (doc_id_prefix, narrative, source_url)."""
    recipient = award.get("Recipient Name") or "Unknown Recipient"
    amount = award.get("_amount_float") or award.get("Award Amount") or 0
    agency = award.get("Awarding Agency") or award.get("Awarding Sub Agency") or "Unknown Agency"
    description = award.get("Description") or "No description available"
    city = award.get("Place of Performance City Name") or ""
    state = award.get("Place of Performance State Code") or ""
    location = f"{city}, {state}".strip(", ") if (city or state) else "location not specified"
    start_date = award.get("Start Date") or "unknown"
    end_date = award.get("End Date") or "unknown"
    naics_desc = award.get("naics_description") or award.get("naics_code") or "infrastructure"
    award_id = award.get("Award ID") or "unknown"
    contract_type_code = award.get("Contract Award Type") or ""
    contract_type = AWARD_TYPE_LABELS.get(contract_type_code, contract_type_code)

    narrative = (
        f"Federal contract award: {recipient} was awarded ${amount:,.0f} "
        f"by {agency} for {description} in {location}. "
        f"Contract period: {start_date} to {end_date}. "
        f"NAICS: {naics_desc}. Award ID: {award_id}."
        f"{f' Contract type: {contract_type}.' if contract_type else ''}"
    )
    safe_id = award_id.replace("/", "-").replace(" ", "_").replace(":", "-")
    source_url = f"https://www.usaspending.gov/award/{award_id}"
    return f"award_{safe_id}", narrative, source_url


def fetch_and_store(run_id: str) -> dict:
    awards = _fetch_all_awards()
    if not awards:
        return {"prepared_blob_path": None, "record_count": 0}

    write_parquet_records(RAW_CONTAINER, f"awards/{run_id}.parquet", awards)

    prepared = []
    for award in awards:
        doc_id_prefix, narrative, source_url = _build_narrative(award)
        prepared.append({
            "doc_id_prefix": doc_id_prefix,
            "narrative": narrative,
            "domain": "business_development",
            "document_type": "contract_award",
            "source": "USASpending.gov",
            "source_url": source_url,
        })

    prepared_blob_path = f"awards/{run_id}.json"
    write_json_records(PREPARED_CONTAINER, prepared_blob_path, prepared)
    return {"prepared_blob_path": prepared_blob_path, "record_count": len(awards)}
