"""Tests for the get_contract_awards tool.

All external HTTP calls are mocked with respx so no real credentials
or network access are required (USASpending.gov is a public API — no auth needed).
"""

import logging
import os
import sys
from typing import get_args, get_type_hints

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

from unittest.mock import patch

import respx
from httpx import Response

from tools.contract_awards import (
    ContractAwardsInput,
    USASPENDING_URL,
    get_contract_awards,
)
from main import get_contract_awards as mcp_get_contract_awards


# ---------------------------------------------------------------------------
# Shared fixture helpers
# ---------------------------------------------------------------------------


def _make_award(
    award_id: str = "CONT_AWD_W9126G21C0011",
    recipient_name: str = "ACME CONSTRUCTION LLC",
    award_amount: float = 4_500_000.0,
    awarding_agency: str = "DEPARTMENT OF TRANSPORTATION",
    awarding_sub_agency: str = "FEDERAL HIGHWAY ADMINISTRATION",
    description: str = "BRIDGE REHABILITATION PROJECT",
    contract_type: str = "D",
    state_code: str = "TX",
    city_name: str = "Austin",
    naics_description: str = "Highway, Street, and Bridge Construction",
    start_date: str = "2023-01-15",
    end_date: str = "2024-06-30",
) -> dict:
    return {
        "Award ID": award_id,
        "Recipient Name": recipient_name,
        "recipient_id": "abc123",
        "Award Amount": award_amount,
        "Total Outlays": award_amount * 0.9,
        "Description": description,
        "Start Date": start_date,
        "End Date": end_date,
        "Awarding Agency": awarding_agency,
        "Awarding Sub Agency": awarding_sub_agency,
        "Contract Award Type": contract_type,
        "Place of Performance State Code": state_code,
        "Place of Performance City Name": city_name,
        "naics_code": "237310",
        "naics_description": naics_description,
    }


def _usaspending_response(awards: list) -> dict:
    """Build a minimal USASpending spending_by_award JSON response."""
    return {
        "results": awards,
        "page_metadata": {
            "page": 1,
            "count": len(awards),
            "next": None,
            "previous": None,
            "hasNext": False,
        },
    }


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


def test_mcp_query_parameter_has_search_guidance():
    """The exposed MCP schema must tell the model what the required query means."""
    query_annotation = get_type_hints(mcp_get_contract_awards, include_extras=True)["query"]
    metadata = get_args(query_annotation)[1:]

    field = next((item for item in metadata if hasattr(item, "description")), None)
    assert field is not None
    assert field.description == (
        "Natural-language search query, e.g. 'bridge rehabilitation', "
        "'water treatment plant', 'highway expansion'. Drives recipient/description keyword match."
    )


async def test_successful_award_results():
    """Mock USASpending returning 2 awards. Assert both are normalized with the
    expected fields: recipient_name, award_amount_usd, _source, and a correct
    usaspending_permalink containing the award ID."""
    awards = [
        _make_award(award_id="CONT_AWD_001", recipient_name="BRIDGE CORP", award_amount=5_000_000.0),
        _make_award(award_id="CONT_AWD_002", recipient_name="ROAD BUILDERS INC", award_amount=2_000_000.0),
    ]

    with respx.mock as mock:
        mock.post(USASPENDING_URL).mock(
            return_value=Response(200, json=_usaspending_response(awards))
        )

        inp = ContractAwardsInput(query="bridge rehabilitation Texas")
        result = await get_contract_awards(inp)

    assert result["kind"] == "contract_awards"
    assert result["schema_version"] == "1.0"
    assert result["status"] == "ok"
    items = result["items"]
    assert len(items) == 2
    assert result["meta"]["returned_count"] == 2

    first = items[0]
    assert first["recipient_name"] == "BRIDGE CORP"
    assert first["award_amount_usd"] == 5_000_000.0
    assert first["_source"] == "USASpending.gov"
    assert "CONT_AWD_001" in first["usaspending_permalink"]

    second = items[1]
    assert second["recipient_name"] == "ROAD BUILDERS INC"
    assert second["award_amount_usd"] == 2_000_000.0
    assert second["_source"] == "USASpending.gov"
    assert "CONT_AWD_002" in second["usaspending_permalink"]


async def test_geography_filter_narrows_results():
    """When geography='TX' is passed, the request body sent to USASpending must
    include place_of_performance_locations with country=USA and state=TX."""
    awards = [
        _make_award(award_id="CONT_AWD_TX1", state_code="TX"),
        _make_award(award_id="CONT_AWD_TX2", state_code="TX"),
    ]

    captured_request_body: dict = {}

    def capture_and_respond(request):
        import json
        captured_request_body.update(json.loads(request.content))
        return Response(200, json=_usaspending_response(awards))

    with respx.mock as mock:
        mock.post(USASPENDING_URL).mock(side_effect=capture_and_respond)

        inp = ContractAwardsInput(query="highway construction", geography="TX")
        result = await get_contract_awards(inp)

    assert result["status"] == "ok"
    assert len(result["items"]) == 2

    # Verify the request body included the geography filter
    locations = captured_request_body["filters"]["place_of_performance_locations"]
    assert len(locations) == 1
    assert locations[0]["country"] == "USA"
    assert locations[0]["state"] == "TX"


async def test_api_error_returns_structured_error():
    """Mock USASpending returning 500. Assert result is a dict with 'error' key
    and 'retriable': True (server error is considered retriable)."""
    with respx.mock as mock:
        mock.post(USASPENDING_URL).mock(
            return_value=Response(500, text="Internal Server Error")
        )

        inp = ContractAwardsInput(query="water infrastructure")
        result = await get_contract_awards(inp)

    assert result["kind"] == "contract_awards"
    assert result["status"] == "error"
    assert result["items"] == []
    errors = result["meta"]["partial_errors"]
    assert len(errors) == 1
    assert errors[0]["code"] == "http_500"
    assert errors[0]["retriable"] is True


async def test_api_error_logs_response_body_for_debugging():
    """Regression test: a non-2xx response must be logged with the actual
    response body (via log_external_api_failure), not just a bare status
    code — this was the exact gap that made the USASpending 422 the user
    saw unrecoverable from Datadog."""
    with (
        patch("tools.contract_awards.log_external_api_failure") as mock_log,
        respx.mock as mock,
    ):
        mock.post(USASPENDING_URL).mock(
            return_value=Response(422, text="Unprocessable Entity: invalid naics_codes")
        )

        inp = ContractAwardsInput(query="water infrastructure")
        await get_contract_awards(inp)

    mock_log.assert_called_once()
    kwargs = mock_log.call_args.kwargs
    assert kwargs["source"] == "usaspending"
    assert kwargs["status_code"] == 422
    assert "invalid naics_codes" in kwargs["body"]


async def test_contract_award_logs_exclude_query_and_provider_body(caplog):
    query = "PRIVATE-CONTRACT-QUERY"
    provider_body = "PRIVATE-USASPENDING-BODY"
    with caplog.at_level(logging.INFO, logger="tools.contract_awards"):
        with respx.mock as mock:
            mock.post(USASPENDING_URL).mock(return_value=Response(422, text=provider_body))
            await get_contract_awards(ContractAwardsInput(query=query, geography="PRIVATE-GEOGRAPHY"))

    logged = "\n".join(record.getMessage() for record in caplog.records)
    assert query not in logged
    assert "PRIVATE-GEOGRAPHY" not in logged
    assert provider_body not in logged


async def test_duplicate_award_ids_are_deduped_first_seen_wins():
    """USASpending pagination/plumbing can surface the same award_id twice.
    The artifact envelope must keep only the first occurrence."""
    awards = [
        _make_award(award_id="CONT_AWD_DUP", recipient_name="FIRST SEEN CORP"),
        _make_award(award_id="CONT_AWD_DUP", recipient_name="SECOND SEEN CORP"),
        _make_award(award_id="CONT_AWD_UNIQUE", recipient_name="UNIQUE CORP"),
    ]

    with respx.mock as mock:
        mock.post(USASPENDING_URL).mock(
            return_value=Response(200, json=_usaspending_response(awards))
        )

        inp = ContractAwardsInput(query="bridge rehabilitation Texas")
        result = await get_contract_awards(inp)

    items = result["items"]
    assert len(items) == 2
    assert result["meta"]["returned_count"] == 2
    award_ids = [item["award_id"] for item in items]
    assert award_ids.count("CONT_AWD_DUP") == 1
    dup_item = next(item for item in items if item["award_id"] == "CONT_AWD_DUP")
    assert dup_item["recipient_name"] == "FIRST SEEN CORP"
