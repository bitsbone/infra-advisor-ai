"""Tests for safe, trace-correlated external API failure summaries."""

import os
import sys
from unittest.mock import MagicMock, patch

os.environ.setdefault("DD_AGENT_HOST", "localhost")
os.environ.setdefault("DD_TRACE_ENABLED", "false")
os.environ.setdefault("DD_DOGSTATSD_PORT", "8125")

_SRC = os.path.join(os.path.dirname(__file__), "..", "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

import logging

from observability.tracing import _redact, log_external_api_failure


# ---------------------------------------------------------------------------
# _redact
# ---------------------------------------------------------------------------


def test_redact_strips_api_key_from_url():
    url = "https://api.sam.gov/opportunities/v2/search?api_key=SECRET123&limit=25"
    redacted = _redact(url)
    assert "SECRET123" not in redacted
    assert "api_key=***" in redacted
    assert "limit=25" in redacted  # non-secret params untouched


def test_redact_strips_apikey_variant():
    url = "https://api.eia.gov/v2/electricity/data/?apikey=OTHERSECRET&frequency=annual"
    redacted = _redact(url)
    assert "OTHERSECRET" not in redacted
    assert "apikey=***" in redacted


def test_redact_applies_to_body_text_defensively():
    body = 'Error processing request for api_key=LEAKED_KEY_VALUE — invalid parameter'
    redacted = _redact(body)
    assert "LEAKED_KEY_VALUE" not in redacted
    assert "api_key=***" in redacted


def test_redact_is_case_insensitive():
    url = "https://example.com/?API_KEY=SECRET"
    redacted = _redact(url)
    assert "SECRET" not in redacted


def test_redact_noop_when_no_secret_present():
    url = "https://api.usaspending.gov/api/v2/search/spending_by_award/"
    assert _redact(url) == url


# ---------------------------------------------------------------------------
# log_external_api_failure
# ---------------------------------------------------------------------------


def test_logs_only_body_fingerprint_not_payload():
    log = MagicMock(spec=logging.Logger)
    log_external_api_failure(
        log,
        source="usaspending",
        tool_name="get_contract_awards",
        status_code=422,
        body="Unprocessable Entity: invalid NAICS code",
    )
    log.warning.assert_called_once()
    call_args = log.warning.call_args[0]
    # First positional arg is the format string; the rest are %-args.
    assert 422 in call_args
    assert not any("Unprocessable Entity: invalid NAICS code" in str(a) for a in call_args)
    assert len("Unprocessable Entity: invalid NAICS code") in call_args


def test_redacts_secret_before_logging():
    log = MagicMock(spec=logging.Logger)
    log_external_api_failure(
        log,
        source="samgov",
        tool_name="get_procurement_opportunities",
        status_code=401,
        body="Unauthorized",
        url="https://api.sam.gov/opportunities/v2/search?api_key=TOPSECRET&limit=25",
    )
    call_args = log.warning.call_args[0]
    assert not any("TOPSECRET" in str(a) for a in call_args)


def test_removes_all_url_query_and_fragment_data():
    log = MagicMock(spec=logging.Logger)
    with patch("observability.tracing.tag_span") as mock_tag_span:
        log_external_api_failure(
            log,
            source="provider",
            tool_name="tool",
            url="https://api.example.test/path?signature=TOPSECRET&tenant=user#fragment",
        )

    assert not any("TOPSECRET" in str(value) or "tenant=user" in str(value) for value in log.warning.call_args[0])
    assert any(call.args == ("error.url", "https://api.example.test/path") for call in mock_tag_span.call_args_list)


def test_records_long_body_size_without_body():
    log = MagicMock(spec=logging.Logger)
    long_body = "x" * 5000
    log_external_api_failure(
        log, source="eia", tool_name="get_energy_infrastructure", body=long_body
    )
    call_args = log.warning.call_args[0]
    assert 5000 in call_args
    assert not any(long_body in str(a) for a in call_args)


def test_accepts_error_string_for_sdk_mediated_failures():
    """project_knowledge.py / water_infrastructure.py's Azure SDK paths have no
    raw HTTP response — only an exception string."""
    log = MagicMock(spec=logging.Logger)
    log_external_api_failure(
        log,
        source="azure_ai_search",
        tool_name="search_project_knowledge",
        error="Index 'infra-advisor-knowledge' not found",
    )
    log.warning.assert_called_once()
    call_args = log.warning.call_args[0]
    assert not any("Index 'infra-advisor-knowledge' not found" in str(value) for value in call_args)
    assert len("Index 'infra-advisor-knowledge' not found") in call_args


def test_exception_text_is_fingerprinted_not_logged_or_tagged():
    log = MagicMock(spec=logging.Logger)
    sentinel = "provider rejected https://example.test/path?sig=PRIVATE-SIGNATURE"

    with patch("observability.tracing.tag_span") as mock_tag_span:
        log_external_api_failure(
            log,
            source="provider",
            tool_name="tool",
            error=sentinel,
        )

    assert not any(sentinel in str(value) or "PRIVATE-SIGNATURE" in str(value) for value in log.warning.call_args[0])
    assert not any(sentinel in str(call.args) or "PRIVATE-SIGNATURE" in str(call.args) for call in mock_tag_span.call_args_list)
    assert any(call.args[0] == "error.message_bytes" for call in mock_tag_span.call_args_list)
    assert any(call.args[0] == "error.message_sha256" for call in mock_tag_span.call_args_list)


def test_redacts_secret_from_exception_message_and_span():
    log = MagicMock(spec=logging.Logger)
    with patch("observability.tracing.tag_span") as mock_tag_span:
        log_external_api_failure(log, source="samgov", tool_name="get_procurement_opportunities", error="GET https://api.sam.gov/search?api_key=SECRET_VALUE failed")
    assert not any("SECRET_VALUE" in str(a) for a in log.warning.call_args[0])
    assert not any("SECRET_VALUE" in str(c.args) for c in mock_tag_span.call_args_list)


def test_tags_active_span_when_present():
    log = MagicMock(spec=logging.Logger)
    with patch("observability.tracing.tag_span") as mock_tag_span:
        log_external_api_failure(
            log,
            source="usaspending",
            tool_name="get_contract_awards",
            status_code=422,
            body="Unprocessable Entity",
        )
    tagged = {call.args[0]: call.args[1] for call in mock_tag_span.call_args_list}
    assert tagged["error.source"] == "usaspending"
    assert tagged["error.tool"] == "get_contract_awards"
    assert tagged["error.status_code"] == 422
    assert tagged["error.response_bytes"] == len("Unprocessable Entity")
    assert "error.response_sha256" in tagged
    assert "error.response_body" not in tagged


def test_no_op_span_tagging_when_no_active_span():
    """tag_span itself is a no-op when tracer.current_span() is None — confirm
    log_external_api_failure doesn't raise even with no active trace."""
    log = MagicMock(spec=logging.Logger)
    log_external_api_failure(
        log, source="usaspending", tool_name="get_contract_awards", status_code=500
    )
    log.warning.assert_called_once()
