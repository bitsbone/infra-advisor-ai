import json
import os
import sys

_SRC = os.path.join(os.path.dirname(__file__), "..", "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

from artifacts import extract_chat_artifact, extract_chat_artifact_source_urls  # noqa: E402


def _item():
    return {
        "id": "sam.gov:SAMPLE-1",
        "provider": "sam.gov",
        "provider_id": "SAMPLE-1",
        "opportunity_type": "contract",
        "title": "Resilience assessment",
        "agency": {"name": "Example Agency", "code": None},
        "summary": "Sanitized summary",
        "status": "posted",
        "posted_at": "01/10/2026",
        "deadline_at": "2026-02-28",
        "location": {"state_code": "TX", "state_name": "Texas", "city": None},
        "classifications": {"naics": ["541330"], "assistance_listing": [], "set_aside": None},
        "funding": {"currency": "USD", "minimum": 1000, "maximum": 2000, "total": 1500, "expected_awards": 1},
        "source": {"url": "https://sam.gov/opportunities/example?api_key=secret#details", "retrieved_at": "2026-01-15T12:00:00Z"},
        "data_quality": {"missing_fields": []},
    }


def _artifact(items=None):
    values = items or []
    counts = {provider: sum(item.get("provider") == provider for item in values) for provider in ("sam.gov", "grants.gov")}
    counts = {provider: count for provider, count in counts.items() if count}
    return {"kind": "procurement_opportunities", "schema_version": "1.0", "status": "ok", "generated_at": "2026-01-15T12:00:00Z", "items": values, "meta": {"returned_count": len(values), "provider_counts": counts, "truncated": False, "partial_errors": []}}


def test_extracts_and_correlates_supported_artifact():
    result = extract_chat_artifact(json.dumps(_artifact()), "get_procurement_opportunities", "call-1")
    assert result is not None
    assert result["tool_call_id"] == "call-1"


def test_rejects_unknown_version_and_oversized_final_artifact():
    unknown = _artifact()
    unknown["schema_version"] = "2.0"
    assert extract_chat_artifact(unknown) is None
    oversized = _artifact([{"summary": "x" * (64 * 1024)}])
    assert extract_chat_artifact(oversized, "tool", "call") is None


def test_extracts_from_mcp_call_tool_result_text_and_structured_content():
    artifact = _artifact()
    text_envelope = {"content": [{"type": "text", "text": json.dumps(artifact)}], "isError": False}
    structured_envelope = {"content": [], "structuredContent": artifact, "isError": False}

    assert extract_chat_artifact(json.dumps(text_envelope), "tool", "text-call")["tool_call_id"] == "text-call"
    assert extract_chat_artifact(structured_envelope, "tool", "structured-call")["tool_call_id"] == "structured-call"


def test_does_not_promote_artifacts_from_arbitrary_nested_provider_json():
    assert extract_chat_artifact({"provider_response": {"result": _artifact()}}) is None


def test_procurement_sources_are_exposed_without_query_or_fragment():
    artifact = _artifact([_item()])
    envelope = {"content": [{"type": "text", "text": json.dumps(artifact)}]}

    assert extract_chat_artifact_source_urls(json.dumps(envelope)) == ["https://sam.gov/opportunities/example"]


def test_rebuilds_exact_allowlist_and_strips_nested_sensitive_fields():
    artifact = _artifact([_item()])
    artifact["api_key"] = "top-level-secret"
    artifact["items"][0]["raw_provider_payload"] = {"contact": "private"}
    artifact["items"][0]["agency"]["api_key"] = "nested-secret"
    artifact["items"][0]["source"]["contact"] = {"email": "private@example.com"}
    artifact["meta"]["debug"] = {"authorization": "Bearer secret"}

    result = extract_chat_artifact(artifact, "get_procurement_opportunities", "call-1")

    assert result is not None
    serialized = json.dumps(result)
    assert "secret" not in serialized
    assert "contact" not in serialized
    assert "raw_provider_payload" not in serialized
    assert set(result) == {"kind", "schema_version", "status", "generated_at", "items", "meta", "tool_name", "tool_call_id"}
    assert set(result["items"][0]) == {"id", "provider", "provider_id", "opportunity_type", "title", "agency", "summary", "status", "posted_at", "deadline_at", "location", "classifications", "funding", "source", "data_quality"}
    assert set(result["items"][0]["agency"]) == {"name", "code"}
    assert set(result["meta"]) == {"returned_count", "provider_counts", "truncated", "partial_errors"}
    assert result["items"][0]["posted_at"] == "2026-01-10"
    assert result["items"][0]["source"]["url"] == "https://sam.gov/opportunities/example"


def test_rejects_invalid_provider_type_date_amount_and_url():
    mutations = [
        lambda item: item.update(provider="unknown.example"),
        lambda item: item.update(opportunity_type="grant"),
        lambda item: item.update(deadline_at="not-a-date"),
        lambda item: item["funding"].update(total=-1),
        lambda item: item["source"].update(url="https://user:secret@sam.gov/private"),
        lambda item: item["source"].update(url="javascript:alert(1)"),
    ]
    for mutate in mutations:
        item = _item()
        mutate(item)
        assert extract_chat_artifact(_artifact([item])) is None


def test_rejects_invalid_counts_lengths_and_partial_error_values():
    artifact = _artifact([_item()])
    artifact["meta"]["provider_counts"] = {"sam.gov": 2}
    assert extract_chat_artifact(artifact) is None

    artifact = _artifact([_item()])
    artifact["items"][0]["title"] = "x" * 501
    assert extract_chat_artifact(artifact) is None

    artifact = _artifact([_item()])
    artifact["meta"]["partial_errors"] = [{"provider": "evil.example", "code": "failed", "retriable": False}]
    assert extract_chat_artifact(artifact) is None


# ---------------------------------------------------------------------------
# contract_awards kind
# ---------------------------------------------------------------------------


def _award_item(award_id="CONT_AWD_001", recipient_name="BRIDGE CORP"):
    return {
        "award_id": award_id,
        "recipient_name": recipient_name,
        "award_amount_usd": 5_000_000.0,
        "awarding_agency": "DEPARTMENT OF TRANSPORTATION",
        "awarding_sub_agency": "FEDERAL HIGHWAY ADMINISTRATION",
        "description": "BRIDGE REHABILITATION PROJECT",
        "place_of_performance": "Austin TX",
        "start_date": "2023-01-15",
        "end_date": "2024-06-30",
        "naics_description": "Highway, Street, and Bridge Construction",
        "contract_type": "Definitive Contract",
        "usaspending_permalink": f"https://www.usaspending.gov/award/{award_id}",
        "_source": "USASpending.gov",
        "source": {"name": "USASpending.gov", "retrieved_at": "2026-01-15T12:00:00Z"},
    }


def _contract_awards_artifact(items=None):
    values = items or []
    return {
        "kind": "contract_awards",
        "schema_version": "1.0",
        "status": "ok",
        "generated_at": "2026-01-15T12:00:00Z",
        "items": values,
        "meta": {"returned_count": len(values), "truncated": False, "partial_errors": []},
    }


def test_extracts_contract_awards_artifact():
    result = extract_chat_artifact(json.dumps(_contract_awards_artifact([_award_item()])), "get_contract_awards", "call-1")
    assert result is not None
    assert result["kind"] == "contract_awards"
    assert result["tool_call_id"] == "call-1"
    assert result["items"][0]["recipient_name"] == "BRIDGE CORP"


def test_dedupes_duplicate_award_ids_first_seen_wins_at_extraction_layer():
    items = [
        _award_item(award_id="CONT_AWD_DUP", recipient_name="FIRST SEEN CORP"),
        _award_item(award_id="CONT_AWD_DUP", recipient_name="SECOND SEEN CORP"),
        _award_item(award_id="CONT_AWD_UNIQUE", recipient_name="UNIQUE CORP"),
    ]
    artifact = _contract_awards_artifact(items)
    artifact["meta"]["returned_count"] = len(items)  # simulate an un-deduped upstream count
    result = extract_chat_artifact(artifact, "get_contract_awards", "call-1")

    assert result is not None
    assert len(result["items"]) == 2
    assert result["meta"]["returned_count"] == 2
    dup = next(i for i in result["items"] if i["award_id"] == "CONT_AWD_DUP")
    assert dup["recipient_name"] == "FIRST SEEN CORP"


def test_contract_awards_permalink_is_the_exposed_source_url():
    artifact = _contract_awards_artifact([_award_item()])
    envelope = {"content": [{"type": "text", "text": json.dumps(artifact)}]}
    assert extract_chat_artifact_source_urls(json.dumps(envelope)) == ["https://www.usaspending.gov/award/CONT_AWD_001"]


def test_contract_awards_rejects_wrong_source_name():
    item = _award_item()
    item["source"]["name"] = "not-usaspending"
    assert extract_chat_artifact(_contract_awards_artifact([item])) is None
