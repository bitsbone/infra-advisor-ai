import ddtrace.auto  # must be first import

import logging
import re
import hashlib
from urllib.parse import urlsplit, urlunsplit

from ddtrace import tracer


def current_trace_id() -> str | None:
    """Return the current Datadog trace ID as a hex string, or None if no active span."""
    span = tracer.current_span()
    if span is None:
        return None
    return format(span.trace_id, "032x")


def tag_span(key: str, value: str | int | float) -> None:
    """Set a tag on the current active span, if one exists."""
    span = tracer.current_span()
    if span is not None:
        span.set_tag(key, value)


# ---------------------------------------------------------------------------
# External API failure logging — bounded metadata only
# ---------------------------------------------------------------------------
#
# Every failure emits a trace-correlated summary with status, byte count, and a
# body fingerprint. Raw provider content is deliberately excluded.

# EIA and SAM.gov both pass their key as ?api_key=... — the only secret that
# can appear in a URL across these tools (headers are never logged, so
# header-based secrets like ERCOT's Ocp-Apim-Subscription-Key never reach
# this code path).
_SECRET_PARAM_RE = re.compile(r"(?i)\b(api_key|apikey)=[^&\s\"']+")


def _redact(text: str) -> str:
    """Replace known secret query-param values with a placeholder. Applied to
    both URLs and body text defensively, in case a response ever echoes
    request params back."""
    return _SECRET_PARAM_RE.sub(r"\1=***", text)


def _sanitize_url(value: str) -> str:
    """Remove every query and fragment before a URL reaches telemetry."""
    parts = urlsplit(value)
    if parts.scheme and parts.netloc:
        return urlunsplit((parts.scheme, parts.netloc, parts.path, "", ""))
    return _redact(value)


def log_external_api_failure(
    log: logging.Logger,
    *,
    source: str,
    tool_name: str,
    status_code: int | None = None,
    body: str | bytes | None = None,
    url: str | None = None,
    error: str | None = None,
) -> None:
    """Log + span-tag safe failure metadata without retaining response payloads.

    `body` is the raw response text (or the text that failed to parse, for
    post-parse failures like malformed JSON). `error` is an exception string
    for SDK-mediated failures (Azure OpenAI / Azure AI Search) where there's
    no raw HTTP response to read. Both values are reduced to byte counts and
    fingerprints; provider text is never emitted as a log or span attribute.
    """
    text = body.decode("utf-8", errors="replace") if isinstance(body, bytes) else (body or "")
    safe_url = _sanitize_url(url) if url else None
    body_bytes = text.encode("utf-8", errors="replace")
    body_size = len(body_bytes)
    body_sha256 = hashlib.sha256(body_bytes).hexdigest() if body_bytes else None
    error_bytes = (error or "").encode("utf-8", errors="replace")
    error_size = len(error_bytes)
    error_sha256 = hashlib.sha256(error_bytes).hexdigest() if error_bytes else None

    log.warning(
        "external API failure: source=%s tool=%s status=%s url=%s error_bytes=%s error_sha256=%s response_bytes=%s response_sha256=%s",
        source, tool_name, status_code, safe_url, error_size, error_sha256, body_size, body_sha256,
    )

    tag_span("error.source", source)
    tag_span("error.tool", tool_name)
    if status_code is not None:
        tag_span("error.status_code", status_code)
    if safe_url:
        tag_span("error.url", safe_url)
    if error_bytes:
        tag_span("error.message_bytes", error_size)
        tag_span("error.message_sha256", error_sha256 or "")
    if body_bytes:
        tag_span("error.response_bytes", body_size)
        tag_span("error.response_sha256", body_sha256 or "")
