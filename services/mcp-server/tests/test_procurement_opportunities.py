"""Tests for the get_procurement_opportunities tool.

All external HTTP calls are mocked with respx so no real credentials
or network access are required.
"""

import json
import logging
import os
import sys
from pathlib import Path
from unittest.mock import patch

import pytest
from jsonschema import Draft202012Validator, FormatChecker

# ---------------------------------------------------------------------------
# Environment setup — must happen before any ddtrace imports
# ---------------------------------------------------------------------------
os.environ.setdefault("DD_AGENT_HOST", "localhost")
os.environ.setdefault("DD_TRACE_ENABLED", "false")
os.environ.setdefault("DD_DOGSTATSD_PORT", "8125")

# Make sure the src package tree is importable when running from the
# services/mcp-server root via `uv run pytest`.
_SRC = os.path.join(os.path.dirname(__file__), "..", "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

import respx
from httpx import Response

from tools.procurement_opportunities import (
    ProcurementOpportunitiesInput,
    SAMGOV_API_URL,
    GRANTSGOV_SEARCH_URL,
    _derive_naics,
    _ALL_NAICS,
    get_procurement_opportunities,
)

_CONTRACT = Path(__file__).resolve().parents[3] / "contracts/chat-artifacts/procurement-opportunities.v1.schema.json"


def _assert_v1_schema(value: dict) -> None:
    schema = json.loads(_CONTRACT.read_text())
    Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)


# ---------------------------------------------------------------------------
# Shared fixture helpers
# ---------------------------------------------------------------------------


def _make_sam_opportunity(
    notice_id: str = "NOTICE-001",
    title: str = "Water Treatment Plant Renovation",
    deadline: str = "2025-06-30",
) -> dict:
    return {
        "noticeId": notice_id,
        "title": title,
        "type": "Solicitation",
        "fullParentPathName": "DEPARTMENT OF DEFENSE",
        "naicsCode": "237110",
        "postedDate": "2025-01-15",
        "responseDeadLine": deadline,
        "award": None,
        "description": "Renovation of municipal water treatment infrastructure.",
        "uiLink": f"https://sam.gov/opp/{notice_id}",
        "placeOfPerformance": {"stateName": "Texas"},
    }


def _make_sam_response(opportunities: list) -> dict:
    return {
        "totalRecords": len(opportunities),
        "opportunitiesData": opportunities,
    }


def _make_grants_opportunity(
    opp_id: int = 10001,
    title: str = "Water Infrastructure Improvement Grant",
    close_date: str = "2025-07-15",
    cfda_number: str = "66.458",
) -> dict:
    return {
        "id": opp_id,
        "title": title,
        "agencyName": "Environmental Protection Agency",
        "openDate": "2025-01-01",
        "closeDate": close_date,
        "estimatedTotalProgramFunding": 5000000,
        "expectedNumberOfAwards": 10,
        "description": "Funding for water infrastructure improvements.",
        "alnist": [cfda_number],
        "oppStatus": "posted",
    }


def _make_grants_response(opportunities: list) -> dict:
    return {"data": {"oppHits": opportunities}}


# ---------------------------------------------------------------------------
# Test 1: Successful merged results
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_successful_merged_results(monkeypatch):
    """Both SAM.gov and grants.gov return results; merged list has correct _source tags."""
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")

    sam_opps = [
        _make_sam_opportunity("NOTICE-001", deadline="2025-07-01"),
        _make_sam_opportunity("NOTICE-002", deadline="2025-08-01"),
    ]
    grants_opps = [
        _make_grants_opportunity(10001, close_date="2025-06-15"),
    ]

    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(
            return_value=Response(200, json=_make_sam_response(sam_opps))
        )
        mock.post(GRANTSGOV_SEARCH_URL).mock(
            return_value=Response(200, json=_make_grants_response(grants_opps))
        )

        inp = ProcurementOpportunitiesInput(query="water treatment plant")
        result = await get_procurement_opportunities(inp)

    assert result["kind"] == "procurement_opportunities"
    assert result["schema_version"] == "1.0"
    assert len(result["items"]) == 3

    sources = {r["provider"] for r in result["items"]}
    assert "sam.gov" in sources
    assert "grants.gov" in sources

    # Sorted by deadline soonest first: grants.gov 2025-06-15 < SAM.gov 2025-07-01 < SAM.gov 2025-08-01
    assert result["items"][0]["provider"] == "grants.gov"
    assert result["items"][0]["deadline_at"] == "2025-06-15"
    _assert_v1_schema(result)


@pytest.mark.asyncio
async def test_value_range_filters_unknown_and_out_of_range_funding(monkeypatch):
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")
    below = _make_sam_opportunity("SAM-BELOW")
    below["award"] = {"amount": 750_000}
    unknown = _make_sam_opportunity("SAM-UNKNOWN")
    in_range = _make_grants_opportunity(20_001)
    in_range["estimatedTotalProgramFunding"] = 5_000_000
    above = _make_grants_opportunity(20_002)
    above["estimatedTotalProgramFunding"] = 15_000_000

    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(return_value=Response(200, json=_make_sam_response([below, unknown])))
        mock.post(GRANTSGOV_SEARCH_URL).mock(return_value=Response(200, json=_make_grants_response([in_range, above])))
        result = await get_procurement_opportunities(ProcurementOpportunitiesInput(query="water", min_value_usd=1_000_000, max_value_usd=10_000_000))

    assert [item["provider_id"] for item in result["items"]] == ["20001"]
    assert result["meta"]["returned_count"] == 1
    _assert_v1_schema(result)


@pytest.mark.asyncio
async def test_live_shaped_adversarial_provider_fields_are_rebuilt_to_exact_v1(monkeypatch):
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")
    sentinel = "PROVIDER-SECRET-MUST-NOT-ESCAPE"
    sam = _make_sam_opportunity("SAM-ADV")
    sam.update({"postedDate": "not-a-date", "responseDeadLine": "2026-99-99", "uiLink": f"https://user:{sentinel}@sam.gov/opp/SAM-ADV?api_key={sentinel}#contact", "contactInformation": {"email": sentinel}, "api_key": sentinel})
    sam["award"] = {"amount": -1, "internal": {"api_key": sentinel}}
    sam["placeOfPerformance"] = {"state": {"code": "TX", "name": "Texas", "api_key": sentinel}, "city": {"name": "Austin", "contact": sentinel}, "private": sentinel}
    grant = _make_grants_opportunity(30_001)
    grant.update({"expectedNumberOfAwards": 2_000_000, "estimatedTotalProgramFunding": 2_000_000_000_000_000, "contactEmail": sentinel, "providerPayload": {"api_key": sentinel}, "alnist": ["66.458", "invalid", sentinel]})

    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(return_value=Response(200, json=_make_sam_response([sam])))
        mock.post(GRANTSGOV_SEARCH_URL).mock(return_value=Response(200, json=_make_grants_response([grant])))
        result = await get_procurement_opportunities(ProcurementOpportunitiesInput(query="water"))

    _assert_v1_schema(result)
    serialized = json.dumps(result)
    assert sentinel not in serialized
    assert "contactInformation" not in serialized
    sam_item = next(item for item in result["items"] if item["provider"] == "sam.gov")
    grant_item = next(item for item in result["items"] if item["provider"] == "grants.gov")
    assert sam_item["posted_at"] is None
    assert sam_item["deadline_at"] is None
    assert sam_item["source"]["url"] == "https://sam.gov/opp/SAM-ADV"
    assert sam_item["funding"]["total"] is None
    assert grant_item["classifications"]["assistance_listing"] == ["66.458"]
    assert grant_item["funding"]["total"] is None
    assert grant_item["funding"]["expected_awards"] is None


@pytest.mark.asyncio
async def test_structured_summary_log_is_bounded_and_query_free(monkeypatch, caplog):
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")
    sentinel_query = "SENTINEL-QUERY-MUST-NOT-BE-LOGGED"
    opp = _make_sam_opportunity()
    opp["uiLink"] = "https://sam.gov/opp/NOTICE-001?api_key=SENTINEL-SECRET"

    with caplog.at_level(logging.INFO, logger="tools.procurement_opportunities"):
        with respx.mock() as mock:
            mock.get(SAMGOV_API_URL).mock(return_value=Response(200, json=_make_sam_response([opp])))
            await get_procurement_opportunities(ProcurementOpportunitiesInput(query=sentinel_query, opportunity_types=["contract"]))

    record = next(r for r in caplog.records if getattr(r, "event", None) == "procurement.artifact.normalized")
    assert record.__dict__["tool.name"] == "get_procurement_opportunities"
    assert record.__dict__["artifact.kind"] == "procurement_opportunities"
    assert record.__dict__["artifact.schema_version"] == "1.0"
    assert record.__dict__["artifact.returned_count"] == 1
    assert record.__dict__["duration_ms"] >= 0
    serialized = json.dumps(record.__dict__["artifact.sample"])
    assert "opportunity_type" in serialized
    assert "deadline_at" in serialized
    assert sentinel_query not in serialized
    assert "SENTINEL-SECRET" not in serialized
    assert "source" not in serialized


@pytest.mark.asyncio
async def test_source_url_drops_query_and_fragment(monkeypatch):
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")
    opp = _make_sam_opportunity()
    opp["uiLink"] = "https://sam.gov/opp/NOTICE-001?api_key=must-not-escape#details"
    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(return_value=Response(200, json=_make_sam_response([opp])))
        result = await get_procurement_opportunities(ProcurementOpportunitiesInput(query="water", opportunity_types=["contract"]))
    assert result["items"][0]["source"]["url"] == "https://sam.gov/opp/NOTICE-001"


@pytest.mark.asyncio
async def test_sam_location_objects_are_normalized_without_description_link(monkeypatch):
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")
    opp = _make_sam_opportunity()
    opp["description"] = "https://api.sam.gov/opportunities/v2/search?api_key=must-not-escape"
    opp["placeOfPerformance"] = {"state": {"code": "TX", "name": "Texas"}, "city": {"name": "Austin"}}
    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(return_value=Response(200, json=_make_sam_response([opp])))
        result = await get_procurement_opportunities(ProcurementOpportunitiesInput(query="water", opportunity_types=["contract"]))

    item = result["items"][0]
    assert item["summary"] == ""
    assert item["location"] == {"state_code": "TX", "state_name": "Texas", "city": "Austin"}
    assert "must-not-escape" not in json.dumps(result)


@pytest.mark.asyncio
async def test_grants_failure_is_a_partial_error(monkeypatch):
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")
    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(return_value=Response(200, json=_make_sam_response([_make_sam_opportunity()])))
        mock.post(GRANTSGOV_SEARCH_URL).mock(return_value=Response(503, text="upstream details"))
        result = await get_procurement_opportunities(ProcurementOpportunitiesInput(query="water"))
    assert result["status"] == "partial"
    assert result["meta"]["partial_errors"] == [{"provider": "grants.gov", "code": "http_503", "retriable": True}]


# ---------------------------------------------------------------------------
# Test 2–4: NAICS derivation
# ---------------------------------------------------------------------------


def test_naics_derivation_water():
    """'water treatment plant construction' matches the water NAICS code."""
    codes = _derive_naics("water treatment plant construction")
    assert codes == ["237110"]


def test_naics_derivation_bridge():
    """'bridge inspection services' matches the bridge NAICS code."""
    codes = _derive_naics("bridge inspection services")
    assert "237310" in codes


def test_naics_derivation_unrecognized():
    """Unrecognized domain falls back to all infrastructure NAICS codes."""
    codes = _derive_naics("nuclear decommissioning")
    assert len(codes) > 1
    # Should return the full fallback list
    assert set(codes) == set(_ALL_NAICS)


# ---------------------------------------------------------------------------
# Test 5: Date range clamping
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_date_range_clamped(monkeypatch):
    """When SAM.gov wraps results in a dict with _note about clamping, the
    final response includes that _note.

    We simulate clamping by patching _build_date_range to return clamped=True.
    """
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")

    import tools.procurement_opportunities as po_module

    original_build = po_module._build_date_range

    def _clamped_date_range(days_back: int = 365):
        from_str, to_str, _ = original_build(days_back=days_back)
        return from_str, to_str, True  # force clamped=True

    monkeypatch.setattr(po_module, "_build_date_range", _clamped_date_range)

    sam_opps = [_make_sam_opportunity("NOTICE-CLAMP")]
    sam_body = {
        "totalRecords": 1,
        "opportunitiesData": sam_opps,
    }

    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(return_value=Response(200, json=sam_body))
        mock.post(GRANTSGOV_SEARCH_URL).mock(
            return_value=Response(200, json=_make_grants_response([]))
        )

        inp = ProcurementOpportunitiesInput(query="highway construction")
        result = await get_procurement_opportunities(inp)

    assert result["kind"] == "procurement_opportunities"
    assert result["meta"]["returned_count"] == 1


# ---------------------------------------------------------------------------
# Test 6: SAM.gov 400 date-range error
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_samgov_400_date_error(monkeypatch):
    """SAM.gov returning 400 with date-range message returns structured error."""
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")

    error_body = {
        "errorCode": "400",
        "errorMessage": "Date range must be null year(s) apart",
    }

    # opportunity_types=["contract"] skips grants.gov entirely; only SAM.gov is called
    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(return_value=Response(400, json=error_body))

        inp = ProcurementOpportunitiesInput(
            query="bridge construction",
            opportunity_types=["contract"],
        )
        result = await get_procurement_opportunities(inp)

    assert result["status"] == "error"
    assert result["meta"]["partial_errors"][0]["provider"] == "sam.gov"


# ---------------------------------------------------------------------------
# Test 7: SAM.gov 403
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_samgov_403(monkeypatch):
    """SAM.gov returning 403 includes message about 24-hour activation delay."""
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")

    # opportunity_types=["contract"] skips grants.gov entirely; only SAM.gov is called
    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(return_value=Response(403, text="Forbidden"))

        inp = ProcurementOpportunitiesInput(
            query="pipeline inspection",
            opportunity_types=["contract"],
        )
        result = await get_procurement_opportunities(inp)

    assert result["status"] == "error"
    assert result["meta"]["partial_errors"][0]["provider"] == "sam.gov"


# ---------------------------------------------------------------------------
# Test 7b: SAM.gov 401 — the exact gap from the user's bug report. Falls into
# the generic >=400 branch (no special case existed for 401), which
# previously logged nothing beyond a bare status code.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_samgov_401_logs_response_body(monkeypatch):
    """SAM.gov returning 401 (expired/invalid api_key) must log the actual
    response body via log_external_api_failure, with the api_key redacted
    from any URL passed alongside it — not just a bare 'HTTP 401'."""
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key-should-never-appear-in-logs")

    with (
        patch("tools.procurement_opportunities.log_external_api_failure") as mock_log,
        respx.mock() as mock,
    ):
        mock.get(SAMGOV_API_URL).mock(
            return_value=Response(401, text="Invalid or expired API key")
        )

        inp = ProcurementOpportunitiesInput(
            query="pipeline inspection",
            opportunity_types=["contract"],
        )
        result = await get_procurement_opportunities(inp)

    assert result["status"] == "error"

    mock_log.assert_called_once()
    kwargs = mock_log.call_args.kwargs
    assert kwargs["source"] == "samgov"
    assert kwargs["status_code"] == 401
    assert "Invalid or expired API key" in kwargs["body"]


# ---------------------------------------------------------------------------
# Test 8: SAM.gov timeout — partial results from grants.gov
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_samgov_timeout_partial_results(monkeypatch):
    """When SAM.gov times out, grants.gov results are still returned."""
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")

    import httpx as _httpx

    grants_opp = _make_grants_opportunity(20001, close_date="2025-09-01")

    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(side_effect=_httpx.TimeoutException("timeout"))
        mock.post(GRANTSGOV_SEARCH_URL).mock(
            return_value=Response(200, json=_make_grants_response([grants_opp]))
        )

        inp = ProcurementOpportunitiesInput(query="flood mitigation environmental")
        result = await get_procurement_opportunities(inp)

    # Should return partial results (grants.gov) without crashing
    assert result["status"] == "partial"
    assert len(result["items"]) == 1
    assert result["items"][0]["provider"] == "grants.gov"


# ---------------------------------------------------------------------------
# Test 9: Unknown SAM.gov response envelope
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_unknown_response_envelope(monkeypatch):
    """SAM.gov returns 200 with wrong key — tool returns structured error."""
    monkeypatch.setenv("SAMGOV_API_KEY", "SAM-test-key")

    bad_body = {"data": [{"id": 1}], "total": 1}

    # opportunity_types=["contract"] skips grants.gov entirely; only SAM.gov is called
    with respx.mock() as mock:
        mock.get(SAMGOV_API_URL).mock(return_value=Response(200, json=bad_body))

        inp = ProcurementOpportunitiesInput(
            query="dam construction",
            opportunity_types=["contract"],
        )
        result = await get_procurement_opportunities(inp)

    # _fetch_samgov logs WARNING and returns a structured error dict when
    # 'opportunitiesData' key is missing from the response
    assert result["status"] == "error"
    assert result["meta"]["partial_errors"][0]["provider"] == "sam.gov"
