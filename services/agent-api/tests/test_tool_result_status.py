"""Tests for _tool_result_is_error — the structural (not substring) check
used to classify a tool_call_end chip's status in run_agent_stream.

Regression coverage for a real bug found by reading the code: the prior
implementation did `'"error"' in result_content` (a raw substring search
over the tool's full serialized JSON result). Several MCP tools (e.g.
get_procurement_opportunities) nest real per-source error objects under
meta.partial_errors even on an otherwise-successful, simply-empty-results
call — a substring match would misclassify that as a failed tool call.
"""

import json
import os
import sys

_SRC = os.path.join(os.path.dirname(__file__), "..", "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

from agent import _tool_result_is_error  # noqa: E402


def test_no_error_key_is_not_an_error():
    payload = json.dumps({"items": [], "meta": {"returned_count": 0}})
    assert _tool_result_is_error(payload) is False


def test_nested_error_key_in_unrelated_field_is_not_an_error():
    # The exact regression: "partial_errors" contains the substring "error"
    # but is not itself an error condition for THIS tool call.
    payload = json.dumps({
        "status": "empty",
        "items": [],
        "meta": {"partial_errors": [{"source": "samgov", "error": "rate limited"}]},
    })
    assert _tool_result_is_error(payload) is False


def test_top_level_error_key_is_an_error():
    payload = json.dumps({"error": "BTS ArcGIS HTTP error 500"})
    assert _tool_result_is_error(payload) is True


def test_falsy_top_level_error_key_is_not_an_error():
    # A defensive `"error": null` field documenting the absence of an error
    # must not itself be treated as one.
    payload = json.dumps({"error": None, "items": [1, 2, 3]})
    assert _tool_result_is_error(payload) is False


def test_non_json_payload_is_not_an_error():
    assert _tool_result_is_error("not json at all") is False


def test_json_array_payload_is_not_an_error():
    assert _tool_result_is_error(json.dumps([{"id": 1}, {"id": 2}])) is False
