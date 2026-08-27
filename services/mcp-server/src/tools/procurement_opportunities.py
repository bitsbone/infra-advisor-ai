import ddtrace.auto  # must be first import — enables APM auto-instrumentation

import asyncio
import logging
import math
import os
import re
import time
from datetime import date, datetime, timedelta, timezone
from typing import Any, List, Optional
from urllib.parse import urlsplit, urlunsplit

import httpx
from pydantic import BaseModel

from observability.metrics import emit_external_api, emit_tool_call
from observability.tracing import log_external_api_failure

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

SAMGOV_API_URL = "https://api.sam.gov/opportunities/v2/search"
GRANTSGOV_SEARCH_URL = "https://api.grants.gov/v1/api/search2"
_MAX_RESULTS = 20
_MAX_FUNDING_USD = 1_000_000_000_000_000
_MISSING_FIELDS = {"title", "agency.name", "deadline_at", "source.url", "funding.total"}
_NAICS_RE = re.compile(r"^\d{2,6}$")
_ASSISTANCE_RE = re.compile(r"^\d{2}\.\d{3}$")

# NAICS codes relevant to infrastructure procurement domains
_NAICS_MAP: dict[str, list[str]] = {
    "water": ["237110"],
    "sewer": ["237110"],
    "bridge": ["237310"],
    "highway": ["237310"],
    "road": ["237310"],
    "transportation": ["237310"],
    "power": ["237130"],
    "energy": ["237130"],
    "pipeline": ["237120"],
    "building": ["236220"],
    "environmental": ["562910"],
    "dam": ["237990"],
    "flood": ["237990"],
}
_ALL_NAICS = list({code for codes in _NAICS_MAP.values() for code in codes})

# CFDA programs relevant to infrastructure domains
_CFDA_ALLOWLIST = {"66.458", "66.468", "97.047", "20.933", "14.228", "12.106", "11.300"}

# Full state name -> USPS 2-letter abbreviation. SAM.gov's `state` param
# expects a 2-letter code; this tool previously passed `geography` through
# unmodified, so a query with geography="Texas" silently sent an invalid
# state filter (same root-cause class as contract_awards.py's 422 — see
# that file's comment for the incident this was found from).
_STATE_NAME_TO_ABBREV = {
    "alabama": "AL", "alaska": "AK", "arizona": "AZ", "arkansas": "AR",
    "california": "CA", "colorado": "CO", "connecticut": "CT", "delaware": "DE",
    "florida": "FL", "georgia": "GA", "hawaii": "HI", "idaho": "ID",
    "illinois": "IL", "indiana": "IN", "iowa": "IA", "kansas": "KS",
    "kentucky": "KY", "louisiana": "LA", "maine": "ME", "maryland": "MD",
    "massachusetts": "MA", "michigan": "MI", "minnesota": "MN", "mississippi": "MS",
    "missouri": "MO", "montana": "MT", "nebraska": "NE", "nevada": "NV",
    "new hampshire": "NH", "new jersey": "NJ", "new mexico": "NM", "new york": "NY",
    "north carolina": "NC", "north dakota": "ND", "ohio": "OH", "oklahoma": "OK",
    "oregon": "OR", "pennsylvania": "PA", "rhode island": "RI", "south carolina": "SC",
    "south dakota": "SD", "tennessee": "TN", "texas": "TX", "utah": "UT",
    "vermont": "VT", "virginia": "VA", "washington": "WA", "west virginia": "WV",
    "wisconsin": "WI", "wyoming": "WY", "district of columbia": "DC",
}


def _extract_state(geography: str) -> str | None:
    """Return a 2-letter state abbreviation from a geography string, or None
    if it can't be resolved (caller should omit the state filter rather than
    send SAM.gov something it can't use)."""
    g = geography.strip()
    if len(g) == 2 and g.isalpha():
        return g.upper()
    by_name = _STATE_NAME_TO_ABBREV.get(g.lower())
    if by_name:
        return by_name
    for token in g.split():
        if len(token) == 2 and token.isalpha():
            return token.upper()
    return None


# ---------------------------------------------------------------------------
# Input schema
# ---------------------------------------------------------------------------


class ProcurementOpportunitiesInput(BaseModel):
    query: str
    geography: Optional[str] = None
    naics_codes: Optional[List[str]] = None
    min_value_usd: Optional[int] = None
    max_value_usd: Optional[int] = None
    opportunity_types: Optional[List[str]] = None  # "contract", "grant", or both
    limit: int = 20


def _text(value: Any, limit: int = 500) -> str:
    if value is None or isinstance(value, (dict, list, tuple, set)):
        return ""
    return str(value).strip()[:limit]


def _safe_url(value: Any) -> str | None:
    """Keep only scheme/host/path; provider query strings can contain API keys."""
    if not value:
        return None
    parts = urlsplit(str(value))
    if parts.scheme.lower() not in {"http", "https"} or not parts.hostname:
        return None
    try:
        port = f":{parts.port}" if parts.port is not None else ""
    except ValueError:
        return None
    netloc = f"{parts.hostname}{port}"
    safe = urlunsplit((parts.scheme.lower(), netloc, parts.path, "", ""))
    return safe if len(safe) <= 1000 else None


def _location_value(value: Any, nested_key: str) -> str | None:
    """Normalize SAM.gov's object-or-string location fields."""
    if isinstance(value, dict):
        return _text(value.get(nested_key), 200) or None
    return _text(value, 200) or None


def _date_value(value: Any) -> str | None:
    """Return a schema-valid ISO date/date-time or ``None``."""
    if not isinstance(value, str):
        return None
    candidate = value.strip()
    if not candidate or len(candidate) > 50:
        return None
    try:
        if "T" in candidate:
            datetime.fromisoformat(candidate.replace("Z", "+00:00"))
        else:
            date.fromisoformat(candidate)
    except ValueError:
        return None
    return candidate


def _number(value: Any) -> float | int | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if not math.isfinite(value) or value < 0 or value > _MAX_FUNDING_USD:
        return None
    return value


def _integer(value: Any, maximum: int) -> int | None:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0 or value > maximum:
        return None
    return value


def _code_list(value: Any, pattern: re.Pattern[str]) -> list[str]:
    if not isinstance(value, list):
        return []
    result: list[str] = []
    for raw in value:
        code = str(raw).strip()
        if pattern.fullmatch(code) and code not in result:
            result.append(code)
        if len(result) == 20:
            break
    return result


def _normalize_item(candidate: Any) -> dict[str, Any] | None:
    """Rebuild an exact v1 item from allowlisted fields before returning it."""
    if not isinstance(candidate, dict):
        return None
    provider = candidate.get("provider")
    if provider not in {"sam.gov", "grants.gov"}:
        return None
    provider_id = _text(candidate.get("provider_id"), 200)
    agency = candidate.get("agency") if isinstance(candidate.get("agency"), dict) else {}
    location = candidate.get("location") if isinstance(candidate.get("location"), dict) else {}
    classifications = candidate.get("classifications") if isinstance(candidate.get("classifications"), dict) else {}
    funding = candidate.get("funding") if isinstance(candidate.get("funding"), dict) else {}
    source = candidate.get("source") if isinstance(candidate.get("source"), dict) else {}
    quality = candidate.get("data_quality") if isinstance(candidate.get("data_quality"), dict) else {}
    retrieved_at = _date_value(source.get("retrieved_at"))
    if not retrieved_at or "T" not in retrieved_at:
        retrieved_at = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    missing = []
    raw_missing = quality.get("missing_fields") if isinstance(quality.get("missing_fields"), list) else []
    for value in raw_missing:
        if value in _MISSING_FIELDS and value not in missing:
            missing.append(value)

    normalized = {
        "id": _text(f"{provider}:{provider_id}", 300),
        "provider": provider,
        "provider_id": provider_id,
        "opportunity_type": "contract" if provider == "sam.gov" else "grant",
        "title": _text(candidate.get("title"), 500),
        "agency": {"name": _text(agency.get("name"), 500), "code": _text(agency.get("code"), 100) or None},
        "summary": _text(candidate.get("summary"), 500),
        "status": _text(candidate.get("status"), 100),
        "posted_at": _date_value(candidate.get("posted_at")),
        "deadline_at": _date_value(candidate.get("deadline_at")),
        "location": {
            "state_code": _text(location.get("state_code"), 20) or None,
            "state_name": _text(location.get("state_name"), 200) or None,
            "city": _text(location.get("city"), 200) or None,
        },
        "classifications": {
            "naics": _code_list(classifications.get("naics"), _NAICS_RE),
            "assistance_listing": _code_list(classifications.get("assistance_listing"), _ASSISTANCE_RE),
            "set_aside": _text(classifications.get("set_aside"), 200) or None,
        },
        "funding": {
            "currency": "USD",
            "minimum": _number(funding.get("minimum")),
            "maximum": _number(funding.get("maximum")),
            "total": _number(funding.get("total")),
            "expected_awards": _integer(funding.get("expected_awards"), 1_000_000),
        },
        "source": {"url": _safe_url(source.get("url")), "retrieved_at": retrieved_at},
        "data_quality": {"missing_fields": missing[:20]},
    }
    for field, present in (
        ("title", bool(normalized["title"])),
        ("agency.name", bool(normalized["agency"]["name"])),
        ("deadline_at", bool(normalized["deadline_at"])),
        ("source.url", bool(normalized["source"]["url"])),
        ("funding.total", normalized["funding"]["total"] is not None),
    ):
        if not present and field not in missing:
            missing.append(field)
    normalized["data_quality"]["missing_fields"] = missing[:20]
    return normalized


def _within_value_range(item: dict[str, Any], minimum: int | None, maximum: int | None) -> bool:
    if minimum is None and maximum is None:
        return True
    funding = item["funding"]
    value = funding["total"] if funding["total"] is not None else funding["maximum"] if funding["maximum"] is not None else funding["minimum"]
    if value is None:
        return False
    return (minimum is None or value >= minimum) and (maximum is None or value <= maximum)


def _artifact(
    items: list[dict[str, Any]],
    errors: list[dict[str, Any]],
    requested_limit: int,
    duration_ms: float,
    min_value_usd: int | None = None,
    max_value_usd: int | None = None,
) -> dict[str, Any]:
    """Create the stable, bounded UI contract; never include request payloads or credentials."""
    limit = max(1, min(requested_limit, _MAX_RESULTS))
    normalized = [item for candidate in items if (item := _normalize_item(candidate)) is not None]
    filtered = [item for item in normalized if _within_value_range(item, min_value_usd, max_value_usd)]
    bounded = filtered[:limit]
    counts = {"sam.gov": 0, "grants.gov": 0}
    for item in bounded:
        counts[item["provider"]] += 1
    artifact = {
        "kind": "procurement_opportunities",
        "schema_version": "1.0",
        "tool_name": "get_procurement_opportunities",
        "tool_call_id": None,
        "status": "partial" if errors and bounded else "error" if errors and not bounded else "empty" if not bounded else "ok",
        "generated_at": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "items": bounded,
        "meta": {"returned_count": len(bounded), "provider_counts": counts, "truncated": len(filtered) > limit, "partial_errors": errors[:2]},
    }
    # A small allowlisted sample exposes the normalized shape in Datadog without
    # indexing provider payloads, descriptions, contacts, request filters, or URLs.
    sample = [
        {
            "id": i["id"],
            "provider": i["provider"],
            "opportunity_type": i["opportunity_type"],
            "status": i["status"],
            "state_code": i["location"]["state_code"],
            "deadline_at": i["deadline_at"],
            "funding_total": i["funding"]["total"],
            "missing_fields": i["data_quality"]["missing_fields"],
        }
        for i in bounded[:3]
    ]
    logger.info(
        "Normalized procurement artifact",
        extra={
            "event": "procurement.artifact.normalized",
            "tool.name": "get_procurement_opportunities",
            "artifact.kind": "procurement_opportunities",
            "artifact.schema_version": "1.0",
            "artifact.status": artifact["status"],
            "artifact.returned_count": len(bounded),
            "artifact.provider_counts": counts,
            "artifact.truncated": artifact["meta"]["truncated"],
            "artifact.partial_error_count": len(errors),
            "artifact.sample": sample,
            "duration_ms": round(duration_ms, 2),
        },
    )
    return artifact


def _safe_error_code(error: dict[str, Any]) -> str:
    """Convert provider prose to a stable category; never propagate echoed request data."""
    message = str(error.get("error") or "").lower()
    if "not configured" in message:
        return "not_configured"
    if "403" in message:
        return "forbidden"
    if "400" in message or "date range" in message:
        return "invalid_request"
    if "format unexpected" in message:
        return "unexpected_response"
    http_status = re.search(r"\bhttp\s+(\d{3})\b", message)
    if http_status:
        return f"http_{http_status.group(1)}"
    return "request_failed"


def _sam_item(opp: dict[str, Any], retrieved_at: str) -> dict[str, Any]:
    provider_id = _text(opp.get("noticeId") or opp.get("solicitationNumber"), 200)
    pop = opp.get("placeOfPerformance") if isinstance(opp.get("placeOfPerformance"), dict) else {}
    award = opp.get("award") if isinstance(opp.get("award"), dict) else {}
    item = {
        "id": f"sam.gov:{provider_id}", "provider": "sam.gov", "provider_id": provider_id, "opportunity_type": "contract",
        "title": _text(opp.get("title")), "agency": {"name": _text(opp.get("fullParentPathName") or opp.get("organizationName")), "code": None},
        # SAM.gov's description is commonly an API link rather than prose. Do
        # not persist provider bodies or contact blocks in the chat artifact.
        "summary": "", "status": _text(opp.get("type"), 100).lower() or "unknown",
        "posted_at": opp.get("postedDate") or None, "deadline_at": opp.get("responseDeadLine") or opp.get("archiveDate") or None,
        "location": {"state_code": _location_value(pop.get("state"), "code") or _text(pop.get("stateCode"), 20) or None, "state_name": _location_value(pop.get("state"), "name") or _text(pop.get("stateName"), 200) or None, "city": _location_value(pop.get("city"), "name")},
        "classifications": {"naics": [str(opp["naicsCode"])] if opp.get("naicsCode") else [], "assistance_listing": [], "set_aside": opp.get("typeOfSetAsideDescription") or opp.get("typeOfSetAside")},
        "funding": {"currency": "USD", "minimum": None, "maximum": None, "total": award.get("amount"), "expected_awards": None},
        "source": {"url": _safe_url(opp.get("uiLink") or next(iter(opp.get("resourceLinks") or []), None)), "retrieved_at": retrieved_at},
        "data_quality": {"missing_fields": []},
    }
    item["data_quality"]["missing_fields"] = [k for k, v in {"title": item["title"], "agency.name": item["agency"]["name"], "deadline_at": item["deadline_at"], "source.url": item["source"]["url"]}.items() if not v]
    return item


def _grant_item(opp: dict[str, Any], retrieved_at: str) -> dict[str, Any]:
    provider_id = _text(opp.get("id") or opp.get("number"), 200)
    alnist = opp.get("alnist") if isinstance(opp.get("alnist"), list) else []
    listings = [str(v) for v in alnist if v]
    item = {
        "id": f"grants.gov:{provider_id}", "provider": "grants.gov", "provider_id": provider_id, "opportunity_type": "grant",
        "title": _text(opp.get("title")), "agency": {"name": _text(opp.get("agencyName")), "code": _text(opp.get("agencyCode"), 100) or None},
        "summary": _text(opp.get("description")), "status": _text(opp.get("oppStatus"), 100).lower() or "unknown",
        "posted_at": opp.get("openDate") or None, "deadline_at": opp.get("closeDate") or None,
        "location": {"state_code": None, "state_name": None, "city": None},
        "classifications": {"naics": [], "assistance_listing": listings, "set_aside": None},
        "funding": {"currency": "USD", "minimum": None, "maximum": None, "total": opp.get("estimatedTotalProgramFunding"), "expected_awards": opp.get("expectedNumberOfAwards")},
        "source": {"url": f"https://www.grants.gov/search-results-detail/{provider_id}", "retrieved_at": retrieved_at},
        "data_quality": {"missing_fields": []},
    }
    item["data_quality"]["missing_fields"] = [k for k, v in {"title": item["title"], "agency.name": item["agency"]["name"], "deadline_at": item["deadline_at"]}.items() if not v]
    return item


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _derive_naics(query: str) -> List[str]:
    """Derive relevant NAICS codes from the query text.

    Returns matched codes in order of first match, deduplicated.
    Falls back to all known infrastructure NAICS codes if no term matches.
    """
    q = query.lower()
    codes: list[str] = []
    seen: set[str] = set()
    for term, term_codes in _NAICS_MAP.items():
        if term in q:
            for c in term_codes:
                if c not in seen:
                    codes.append(c)
                    seen.add(c)
    return codes if codes else list(_ALL_NAICS)


def _build_date_range(
    days_back: int = 365,
) -> tuple[str, str, bool]:
    """Return (postedFrom, postedTo, clamped) as mm/dd/yyyy strings.

    Always returns a range <= 365 days. If the requested range exceeds 365
    days, it is clamped and clamped=True is returned.
    """
    today = datetime.now(timezone.utc).date()
    posted_to = today
    posted_from = today - timedelta(days=days_back)

    clamped = False
    delta = (posted_to - posted_from).days
    if delta > 365:
        posted_to = posted_from + timedelta(days=365)
        clamped = True

    return (
        posted_from.strftime("%m/%d/%Y"),
        posted_to.strftime("%m/%d/%Y"),
        clamped,
    )


# ---------------------------------------------------------------------------
# SAM.gov fetch
# ---------------------------------------------------------------------------


async def _fetch_samgov(
    input_data: ProcurementOpportunitiesInput,
    naics_codes: List[str],
) -> list[dict[str, Any]] | dict[str, Any]:
    """Fetch contract opportunities from SAM.gov Opportunities API v2.

    Returns a list of normalised opportunity dicts, an empty-results dict,
    or a structured error dict. Never raises.
    """
    api_key = os.environ.get("SAMGOV_API_KEY", "")
    if not api_key:
        return {"error": "SAMGOV_API_KEY not configured", "retriable": False}

    posted_from, posted_to, clamped = _build_date_range(days_back=364)

    # Build params as list of tuples so httpx sends multiple ptype values
    params: list[tuple[str, str]] = [
        ("limit", str(max(1, min(input_data.limit, _MAX_RESULTS)))),
        ("offset", "0"),
        ("ptype", "o"),
        ("ptype", "p"),
        ("ptype", "k"),
        ("ptype", "r"),
        ("postedFrom", posted_from),
        ("postedTo", posted_to),
        ("api_key", api_key),
    ]

    for code in naics_codes:
        params.append(("ncode", code))

    if input_data.geography:
        state_abbrev = _extract_state(input_data.geography)
        if state_abbrev:
            params.append(("state", state_abbrev))

    api_start = time.monotonic()
    try:
        async with httpx.AsyncClient(timeout=30.0) as client:
            resp = await client.get(SAMGOV_API_URL, params=params)
            api_latency_ms = (time.monotonic() - api_start) * 1000

            if resp.status_code == 400:
                emit_external_api("samgov", api_latency_ms, error_type="http_400")
                try:
                    body = resp.json()
                    error_message = body.get("errorMessage") or body.get("errorCode", str(resp.status_code))
                except Exception:
                    error_message = resp.text

                log_external_api_failure(
                    logger,
                    source="samgov",
                    tool_name="get_procurement_opportunities",
                    status_code=resp.status_code,
                    body=resp.text,
                )

                if "Date range" in str(error_message):
                    return {
                        "error": (
                            "SAM.gov rejected the request: date range must be within 1 year. "
                            f"Raw message: {error_message}"
                        ),
                        "source": "samgov",
                        "retriable": False,
                    }
                return {
                    "error": f"SAM.gov API error 400: {error_message}",
                    "source": "samgov",
                    "retriable": False,
                }

            if resp.status_code == 403:
                emit_external_api("samgov", api_latency_ms, error_type="http_403")
                log_external_api_failure(
                    logger,
                    source="samgov",
                    tool_name="get_procurement_opportunities",
                    status_code=resp.status_code,
                    body=resp.text,
                )
                return {
                    "error": (
                        "SAM.gov API returned 403 — API key may need up to 24 hours to activate "
                        "after registration at api.sam.gov"
                    ),
                    "source": "samgov",
                    "retriable": False,
                }

            if resp.status_code >= 400:
                # Covers 401 (bad/expired api_key) and any other 4xx/5xx not
                # special-cased above — this is the branch that previously
                # swallowed the SAM.gov 401 with zero body visibility.
                emit_external_api("samgov", api_latency_ms, error_type=f"http_{resp.status_code}")
                log_external_api_failure(
                    logger,
                    source="samgov",
                    tool_name="get_procurement_opportunities",
                    status_code=resp.status_code,
                    body=resp.text,
                )
                return {
                    "error": f"SAM.gov API error: HTTP {resp.status_code}",
                    "source": "samgov",
                    "retriable": resp.status_code >= 500,
                }

            emit_external_api("samgov", api_latency_ms)
            body = resp.json()

    except httpx.TimeoutException as exc:
        api_latency_ms = (time.monotonic() - api_start) * 1000
        emit_external_api("samgov", api_latency_ms, error_type="timeout")
        log_external_api_failure(
            logger, source="samgov", tool_name="get_procurement_opportunities", error=str(exc)
        )
        raise  # re-raise so caller can handle partial results

    except httpx.RequestError as exc:
        api_latency_ms = (time.monotonic() - api_start) * 1000
        emit_external_api("samgov", api_latency_ms, error_type="request_error")
        log_external_api_failure(
            logger, source="samgov", tool_name="get_procurement_opportunities", error=str(exc)
        )
        return {"error": f"SAM.gov request failed: {exc}", "source": "samgov", "retriable": True}

    except Exception as exc:
        api_latency_ms = (time.monotonic() - api_start) * 1000
        emit_external_api("samgov", api_latency_ms, error_type="unexpected")
        logger.warning("Unexpected SAM.gov failure: %s", type(exc).__name__)
        log_external_api_failure(
            logger, source="samgov", tool_name="get_procurement_opportunities", error=str(exc)
        )
        return {"error": "Unexpected error querying SAM.gov", "source": "samgov", "retriable": False}

    if "opportunitiesData" not in body:
        logger.warning(
            "SAM.gov response missing 'opportunitiesData' key. Top-level keys: %s",
            list(body.keys()),
        )
        log_external_api_failure(
            logger,
            source="samgov",
            tool_name="get_procurement_opportunities",
            status_code=resp.status_code,
            body=resp.text,
        )
        return {
            "error": "SAM.gov response format unexpected — 'opportunitiesData' key missing",
            "source": "samgov",
            "retriable": False,
            "response_keys": list(body.keys()),
        }

    opportunities = body["opportunitiesData"]
    if not opportunities:
        return {
            "results": [],
            "_note": f"No results found. NAICS codes queried: {naics_codes}",
        }

    retrieved_at = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    results = [_sam_item(opp, retrieved_at) for opp in opportunities]

    if clamped:
        return {"results": results, "_note": "Date range clamped to 365 days maximum."}

    return results


# ---------------------------------------------------------------------------
# grants.gov fetch
# ---------------------------------------------------------------------------


async def _fetch_grantsgov(
    input_data: ProcurementOpportunitiesInput,
) -> list[dict[str, Any]] | dict[str, Any]:
    """Fetch grant opportunities from grants.gov.

    Returns a list of normalised grant dicts (possibly empty). Never raises — on
    any error logs a warning and returns [].
    """
    api_start = time.monotonic()
    try:
        payload = {
            "keyword": input_data.query,
            "oppStatuses": "forecasted|posted",
            "rows": max(1, min(input_data.limit, _MAX_RESULTS)),
        }
        async with httpx.AsyncClient(timeout=20.0) as client:
            resp = await client.post(
                GRANTSGOV_SEARCH_URL,
                json=payload,
                headers={"Content-Type": "application/json"},
            )
            api_latency_ms = (time.monotonic() - api_start) * 1000
            emit_external_api("grantsgov", api_latency_ms)

            if resp.status_code >= 400:
                logger.warning("grants.gov API returned %s", resp.status_code)
                log_external_api_failure(
                    logger,
                    source="grantsgov",
                    tool_name="get_procurement_opportunities",
                    status_code=resp.status_code,
                    body=resp.text,
                )
                return {"error": f"HTTP {resp.status_code}", "source": "grantsgov", "retriable": resp.status_code >= 500}

            body = resp.json()

    except Exception as exc:
        api_latency_ms = (time.monotonic() - api_start) * 1000
        emit_external_api("grantsgov", api_latency_ms, error_type="error")
        logger.warning("grants.gov fetch failed: %s", type(exc).__name__)
        log_external_api_failure(
            logger, source="grantsgov", tool_name="get_procurement_opportunities", error=str(exc)
        )
        return {"error": "request_failed", "source": "grantsgov", "retriable": True}

    data = body.get("data") or {}
    raw_opportunities = data.get("oppHits") if isinstance(data, dict) else []
    if raw_opportunities is None:
        raw_opportunities = []
    retrieved_at = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    return [_grant_item(opp, retrieved_at) for opp in raw_opportunities]


# ---------------------------------------------------------------------------
# Public tool entry point
# ---------------------------------------------------------------------------


async def get_procurement_opportunities(
    input_data: ProcurementOpportunitiesInput,
) -> dict[str, Any]:
    """Search SAM.gov and grants.gov for infrastructure procurement opportunities.

    Queries both sources concurrently and merges results, sorted by deadline
    (soonest first). Supports filtering by geography, NAICS codes, and
    opportunity type ("contract", "grant", or both).

    Returns a unified list of opportunities or a structured error dict.
    Never raises.
    """
    tool_start = time.monotonic()

    naics_codes = input_data.naics_codes or _derive_naics(input_data.query)

    opportunity_types = input_data.opportunity_types or ["contract", "grant"]
    include_contracts = "contract" in opportunity_types
    include_grants = "grant" in opportunity_types

    async def _empty() -> list:
        return []

    # Schedule concurrent fetches (skip whichever source is not requested)
    samgov_coro = _fetch_samgov(input_data, naics_codes) if include_contracts else _empty()
    grantsgov_coro = _fetch_grantsgov(input_data) if include_grants else _empty()

    # Use asyncio.gather to run both fetches concurrently
    sam_result, grants_result = await asyncio.gather(
        samgov_coro, grantsgov_coro, return_exceptions=True
    )

    # Handle SAM.gov result
    sam_error: dict[str, Any] | None = None
    sam_items: list[dict[str, Any]] = []

    if isinstance(sam_result, BaseException):
        # TimeoutException or other exception from SAM.gov
        logger.warning("SAM.gov fetch raised exception: %s", type(sam_result).__name__)
        sam_error = {"error": "request_failed", "source": "samgov", "retriable": True}
    elif isinstance(sam_result, dict):
        if "error" in sam_result:
            sam_error = sam_result
        elif "results" in sam_result:
            # Wrapped results (possibly clamped note or empty)
            sam_items = sam_result["results"]
        # else: unexpected dict shape, treat as empty
    elif isinstance(sam_result, list):
        sam_items = sam_result

    # Handle grants.gov result
    grants_items: list[dict[str, Any]] = []
    grants_error: dict[str, Any] | None = None
    if isinstance(grants_result, BaseException):
        logger.warning("grants.gov fetch raised exception: %s", type(grants_result).__name__)
        grants_error = {"error": "request_failed", "retriable": True}
    elif isinstance(grants_result, list):
        grants_items = grants_result
    elif isinstance(grants_result, dict) and "error" in grants_result:
        grants_error = grants_result

    # Merge
    all_results = sam_items + grants_items

    # Sort by the normalized deadline.
    def _deadline_key(item: dict) -> str:
        return item.get("deadline_at") or "9999"

    all_results.sort(key=_deadline_key)

    total_latency = (time.monotonic() - tool_start) * 1000

    errors = []
    if sam_error:
        errors.append({"provider": "sam.gov", "code": _safe_error_code(sam_error), "retriable": bool(sam_error.get("retriable"))})
    if grants_error:
        errors.append({"provider": "grants.gov", "code": _safe_error_code(grants_error), "retriable": bool(grants_error.get("retriable"))})

    artifact = _artifact(all_results, errors, input_data.limit, total_latency, input_data.min_value_usd, input_data.max_value_usd)
    emit_tool_call(
        "get_procurement_opportunities",
        total_latency,
        "success",
        result_count=artifact["meta"]["returned_count"],
    )
    return artifact
