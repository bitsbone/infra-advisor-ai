import os
import sys
from unittest.mock import patch

import httpx
import pytest
import respx
from httpx import Response

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from domains import fema  # noqa: E402


@pytest.fixture(autouse=True)
def route_requests_through_httpx(monkeypatch):
    # Production code calls requests.get; respx only intercepts httpx's
    # transport. Route requests.get to httpx.get so respx.mock can catch it —
    # same trick services/ingestion/tests/test_fema_refresh.py uses.
    monkeypatch.setattr("requests.get", httpx.get)


def _page(records: list[dict]) -> dict:
    return {"DisasterDeclarationsSummaries": records}


def _record(disaster_number: int, state: str = "TX") -> dict:
    return {
        "disasterNumber": disaster_number,
        "declarationTitle": "Severe Storms",
        "stateCode": state,
        "designatedArea": "Travis (County)",
        "declarationType": "DR",
        "incidentType": "Severe Storm",
        "declarationDate": "2023-05-01T00:00:00.000Z",
        "incidentBeginDate": "2023-04-28T00:00:00.000Z",
        "incidentEndDate": "2023-05-02T00:00:00.000Z",
        "disasterCloseoutDate": "",
        "paDeclarationString": "Yes",
        "hmDeclarationString": "Yes",
        "fipsStateCode": "48",
        "fipsCountyCode": "453",
    }


@patch("domains.fema.write_json_records")
@patch("domains.fema.write_parquet_records")
def test_fetch_and_store_stops_pagination_below_page_size(mock_parquet, mock_json):
    records = [_record(i) for i in range(3)]
    with respx.mock as mock:
        mock.get(fema.FEMA_API_URL).mock(return_value=Response(200, json=_page(records)))
        result = fema.fetch_and_store(run_id="test-run")

    assert result["record_count"] == 3
    assert result["prepared_blob_path"] == "fema/test-run.json"
    mock_parquet.assert_called_once()
    mock_json.assert_called_once()
    prepared = mock_json.call_args.args[2]
    assert len(prepared) == 3
    assert prepared[0]["domain"] == "environmental"
    assert prepared[0]["document_type"] == "disaster_declaration"
    assert prepared[0]["doc_id_prefix"] == "fema_0"
    assert "Severe Storms" in prepared[0]["narrative"]


@patch("domains.fema.write_json_records")
@patch("domains.fema.write_parquet_records")
def test_fetch_and_store_paginates_across_multiple_pages(mock_parquet, mock_json):
    page1 = [_record(i) for i in range(fema.PAGE_SIZE)]
    page2 = [_record(i) for i in range(fema.PAGE_SIZE, fema.PAGE_SIZE + 5)]
    with respx.mock as mock:
        mock.get(fema.FEMA_API_URL).mock(side_effect=[
            Response(200, json=_page(page1)),
            Response(200, json=_page(page2)),
        ])
        result = fema.fetch_and_store(run_id="test-run")

    assert result["record_count"] == fema.PAGE_SIZE + 5


@patch("domains.fema.write_json_records")
@patch("domains.fema.write_parquet_records")
def test_fetch_and_store_returns_empty_result_when_no_records(mock_parquet, mock_json):
    with respx.mock as mock:
        mock.get(fema.FEMA_API_URL).mock(return_value=Response(200, json=_page([])))
        result = fema.fetch_and_store(run_id="test-run")

    assert result == {"prepared_blob_path": None, "record_count": 0}
    mock_parquet.assert_not_called()
    mock_json.assert_not_called()
