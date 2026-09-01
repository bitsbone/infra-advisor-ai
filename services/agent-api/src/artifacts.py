"""Strict extraction of bounded, versioned UI artifacts from MCP tool output.

MCP output is an untrusted boundary. Every accepted artifact is rebuilt from
the v1 allowlist so provider payloads, credentials, contact records, and future
fields cannot leak into chat responses or conversation persistence.
"""

import json
import math
import re
from datetime import date, datetime
from typing import Any
from urllib.parse import urlsplit, urlunsplit

_MAX_BYTES = 64 * 1024
_MAX_ITEMS = 20
_PROVIDERS = {"sam.gov": "contract", "grants.gov": "grant"}
_STATUSES = {"ok", "partial", "empty", "error"}
_MISSING_FIELDS = {"title", "agency.name", "deadline_at", "source.url", "funding.total"}
_DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
_US_DATE_RE = re.compile(r"^\d{2}/\d{2}/\d{4}$")
_DATETIME_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$")
_NAICS_RE = re.compile(r"^\d{2,6}$")
_ASSISTANCE_RE = re.compile(r"^\d{2}\.\d{3}$")
_CURRENCY_RE = re.compile(r"^[A-Z]{3}$")


def _artifact_candidates(content: Any) -> list[Any]:
    """Return only protocol-defined locations that can contain a tool artifact.

    LangChain commonly exposes MCP text blocks directly, while the .NET MCP
    adapter exposes the serialized CallToolResult envelope. Supporting both
    shapes keeps backend behavior consistent without recursively searching
    arbitrary provider JSON for something that merely resembles an artifact.
    """
    raw_candidates = content if isinstance(content, list) else [content]
    candidates = []
    for candidate in raw_candidates:
        if isinstance(candidate, str) and len(candidate.encode("utf-8")) <= _MAX_BYTES:
            try:
                candidate = json.loads(candidate)
            except (TypeError, ValueError):
                pass
        candidates.append(candidate)
    expanded = list(candidates)
    for candidate in candidates:
        if not isinstance(candidate, dict):
            continue
        structured = candidate.get("structuredContent")
        if isinstance(structured, dict):
            expanded.append(structured)
        blocks = candidate.get("content")
        if isinstance(blocks, list):
            expanded.extend(blocks)
    return expanded


def _string(value: Any, maximum: int, *, nullable: bool = False) -> str | None:
    if value is None and nullable:
        return None
    if not isinstance(value, str):
        raise ValueError("expected string")
    normalized = value.strip()
    if len(normalized) > maximum:
        raise ValueError("string exceeds bound")
    return normalized


def _datetime(value: Any) -> str:
    raw = _string(value, 50)
    if not _DATETIME_RE.fullmatch(raw):
        raise ValueError("expected offset-aware ISO date-time")
    try:
        parsed = datetime.fromisoformat(raw[:-1] + "+00:00" if raw.endswith("Z") else raw)
    except ValueError as error:
        raise ValueError("invalid date-time") from error
    if parsed.tzinfo is None:
        raise ValueError("date-time requires an offset")
    return raw


def _date_or_datetime(value: Any) -> str | None:
    if value is None:
        return None
    raw = _string(value, 50)
    try:
        if _DATE_RE.fullmatch(raw):
            date.fromisoformat(raw)
            return raw
        if _US_DATE_RE.fullmatch(raw):
            return datetime.strptime(raw, "%m/%d/%Y").date().isoformat()
        return _datetime(raw)
    except ValueError as error:
        raise ValueError("invalid date or date-time") from error


def _number(value: Any, *, integer: bool = False) -> int | float | None:
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError("expected number")
    if not math.isfinite(value) or value < 0 or value > 1_000_000_000_000_000:
        raise ValueError("number outside bound")
    if integer and (not isinstance(value, int) or value > 1_000_000):
        raise ValueError("expected bounded integer")
    return value


def _string_array(value: Any, maximum_items: int, maximum_length: int, pattern: re.Pattern[str] | None = None) -> list[str]:
    if not isinstance(value, list) or len(value) > maximum_items:
        raise ValueError("invalid array")
    result = []
    for entry in value:
        normalized = _string(entry, maximum_length)
        if pattern and not pattern.fullmatch(normalized):
            raise ValueError("invalid array entry")
        result.append(normalized)
    return result


def _safe_url(value: Any) -> str | None:
    """Keep only scheme/host/port/path; credentials, query, and fragment never cross the boundary."""
    if value is None:
        return None
    raw = _string(value, 2_000)
    try:
        parts = urlsplit(raw)
        _ = parts.port
    except ValueError as error:
        raise ValueError("invalid source URL") from error
    if parts.scheme.lower() not in {"http", "https"} or not parts.netloc or not parts.hostname or parts.username or parts.password:
        raise ValueError("invalid source URL")
    sanitized = urlunsplit((parts.scheme.lower(), parts.netloc, parts.path, "", ""))
    if len(sanitized) > 1_000:
        raise ValueError("source URL exceeds bound")
    return sanitized


def _object(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError("expected object")
    return value


def _required(container: dict[str, Any], name: str) -> Any:
    if name not in container:
        raise ValueError(f"missing required field: {name}")
    return container[name]


def _normalize_item(value: Any) -> dict[str, Any]:
    item = _object(value)
    provider = _string(item.get("provider"), 20)
    opportunity_type = _string(item.get("opportunity_type"), 20)
    if provider not in _PROVIDERS or opportunity_type != _PROVIDERS[provider]:
        raise ValueError("invalid provider or opportunity type")

    agency = _object(item.get("agency"))
    location = _object(item.get("location"))
    classifications = _object(item.get("classifications"))
    funding = _object(item.get("funding"))
    source = _object(item.get("source"))
    data_quality = _object(item.get("data_quality"))
    currency = _string(funding.get("currency"), 3)
    if not _CURRENCY_RE.fullmatch(currency):
        raise ValueError("invalid currency")
    minimum = _number(_required(funding, "minimum"))
    maximum = _number(_required(funding, "maximum"))
    if minimum is not None and maximum is not None and minimum > maximum:
        raise ValueError("minimum funding exceeds maximum")
    missing_fields = _string_array(data_quality.get("missing_fields"), 20, 100)
    if any(field not in _MISSING_FIELDS for field in missing_fields):
        raise ValueError("invalid missing field")

    return {
        "id": _string(item.get("id"), 300),
        "provider": provider,
        "provider_id": _string(item.get("provider_id"), 200),
        "opportunity_type": opportunity_type,
        "title": _string(item.get("title"), 500),
        "agency": {"name": _string(agency.get("name"), 500), "code": _string(_required(agency, "code"), 100, nullable=True)},
        "summary": _string(item.get("summary"), 500),
        "status": _string(item.get("status"), 100),
        "posted_at": _date_or_datetime(_required(item, "posted_at")),
        "deadline_at": _date_or_datetime(_required(item, "deadline_at")),
        "location": {
            "state_code": _string(_required(location, "state_code"), 20, nullable=True),
            "state_name": _string(_required(location, "state_name"), 200, nullable=True),
            "city": _string(_required(location, "city"), 200, nullable=True),
        },
        "classifications": {
            "naics": _string_array(classifications.get("naics"), 20, 6, _NAICS_RE),
            "assistance_listing": _string_array(classifications.get("assistance_listing"), 20, 6, _ASSISTANCE_RE),
            "set_aside": _string(_required(classifications, "set_aside"), 200, nullable=True),
        },
        "funding": {
            "currency": currency,
            "minimum": minimum,
            "maximum": maximum,
            "total": _number(_required(funding, "total")),
            "expected_awards": _number(_required(funding, "expected_awards"), integer=True),
        },
        "source": {"url": _safe_url(_required(source, "url")), "retrieved_at": _datetime(source.get("retrieved_at"))},
        "data_quality": {"missing_fields": missing_fields},
    }


_CONTRACT_AWARD_STATUSES = {"ok", "empty", "error"}


def _normalize_contract_award_item(value: Any) -> dict[str, Any]:
    item = _object(value)
    source = _object(item.get("source"))
    if _string(source.get("name"), 100) != "USASpending.gov":
        raise ValueError("invalid contract award source")
    return {
        "award_id": _string(item.get("award_id"), 200),
        "recipient_name": _string(item.get("recipient_name"), 500),
        "award_amount_usd": _number(item.get("award_amount_usd")),
        "awarding_agency": _string(item.get("awarding_agency"), 500),
        "awarding_sub_agency": _string(item.get("awarding_sub_agency"), 500),
        "description": _string(item.get("description"), 1000),
        "place_of_performance": _string(item.get("place_of_performance"), 200),
        "start_date": _date_or_datetime(item.get("start_date")),
        "end_date": _date_or_datetime(item.get("end_date")),
        "naics_description": _string(item.get("naics_description"), 300),
        "contract_type": _string(item.get("contract_type"), 100),
        "usaspending_permalink": _safe_url(item.get("usaspending_permalink")),
        "source": {"name": "USASpending.gov", "retrieved_at": _datetime(source.get("retrieved_at"))},
    }


def _normalize_contract_awards_artifact(candidate: dict[str, Any], tool_name: str | None, tool_call_id: str | None) -> dict[str, Any]:
    status = _string(candidate.get("status"), 20)
    if status not in _CONTRACT_AWARD_STATUSES:
        raise ValueError("invalid status")
    raw_items = candidate.get("items")
    if not isinstance(raw_items, list) or len(raw_items) > _MAX_ITEMS:
        raise ValueError("invalid items")
    items = [_normalize_contract_award_item(item) for item in raw_items]

    # Dedup by award_id, first-seen-wins — defensive backstop even though the
    # tool itself now dedups too; nothing upstream guarantees uniqueness.
    deduped: list[dict[str, Any]] = []
    seen: set[str] = set()
    for item in items:
        award_id = item["award_id"]
        if award_id and award_id in seen:
            continue
        if award_id:
            seen.add(award_id)
        deduped.append(item)
    items = deduped

    meta = _object(candidate.get("meta"))
    truncated = meta.get("truncated")
    if not isinstance(truncated, bool):
        raise ValueError("invalid truncated flag")
    raw_errors = meta.get("partial_errors")
    if not isinstance(raw_errors, list) or len(raw_errors) > 2:
        raise ValueError("invalid partial errors")
    partial_errors = []
    for raw_error in raw_errors:
        error = _object(raw_error)
        retriable = error.get("retriable")
        if not isinstance(retriable, bool):
            raise ValueError("invalid partial error")
        partial_errors.append({"code": _string(error.get("code"), 100), "retriable": retriable})

    selected_tool_name = tool_name or candidate.get("tool_name")
    if selected_tool_name is not None:
        selected_tool_name = _string(selected_tool_name, 100)
    if tool_call_id is not None:
        tool_call_id = _string(tool_call_id, 200)
    artifact: dict[str, Any] = {
        "kind": "contract_awards",
        "schema_version": "1.0",
        "status": status,
        "generated_at": _datetime(candidate.get("generated_at")),
        "items": items,
        "meta": {"returned_count": len(items), "truncated": truncated, "partial_errors": partial_errors},
    }
    if selected_tool_name is not None:
        artifact["tool_name"] = selected_tool_name
    artifact["tool_call_id"] = tool_call_id
    return artifact


def _normalize_artifact(candidate: dict[str, Any], tool_name: str | None, tool_call_id: str | None) -> dict[str, Any]:
    if candidate.get("kind") != "procurement_opportunities" or candidate.get("schema_version") != "1.0":
        raise ValueError("unsupported artifact")
    status = _string(candidate.get("status"), 20)
    if status not in _STATUSES:
        raise ValueError("invalid status")
    raw_items = candidate.get("items")
    if not isinstance(raw_items, list) or len(raw_items) > _MAX_ITEMS:
        raise ValueError("invalid items")
    items = [_normalize_item(item) for item in raw_items]

    meta = _object(candidate.get("meta"))
    returned_count = meta.get("returned_count")
    if isinstance(returned_count, bool) or not isinstance(returned_count, int) or returned_count != len(items):
        raise ValueError("invalid returned count")
    raw_counts = _object(meta.get("provider_counts"))
    if any(provider not in _PROVIDERS for provider in raw_counts):
        raise ValueError("invalid provider count key")
    actual_counts = {provider: sum(item["provider"] == provider for item in items) for provider in _PROVIDERS}
    provider_counts: dict[str, int] = {}
    for provider, count in raw_counts.items():
        if isinstance(count, bool) or not isinstance(count, int) or count < 0 or count > _MAX_ITEMS or count != actual_counts[provider]:
            raise ValueError("invalid provider count")
        provider_counts[provider] = count
    if sum(provider_counts.values()) != len(items):
        raise ValueError("provider counts do not match items")

    truncated = meta.get("truncated")
    if not isinstance(truncated, bool):
        raise ValueError("invalid truncated flag")
    raw_errors = meta.get("partial_errors")
    if not isinstance(raw_errors, list) or len(raw_errors) > 2:
        raise ValueError("invalid partial errors")
    partial_errors = []
    for raw_error in raw_errors:
        error = _object(raw_error)
        provider = _string(error.get("provider"), 20)
        retriable = error.get("retriable")
        if provider not in _PROVIDERS or not isinstance(retriable, bool):
            raise ValueError("invalid partial error")
        partial_errors.append({"provider": provider, "code": _string(error.get("code"), 100), "retriable": retriable})

    selected_tool_name = tool_name or candidate.get("tool_name")
    if selected_tool_name is not None:
        selected_tool_name = _string(selected_tool_name, 100)
    if tool_call_id is not None:
        tool_call_id = _string(tool_call_id, 200)
    artifact: dict[str, Any] = {
        "kind": "procurement_opportunities",
        "schema_version": "1.0",
        "status": status,
        "generated_at": _datetime(candidate.get("generated_at")),
        "items": items,
        "meta": {"returned_count": returned_count, "provider_counts": provider_counts, "truncated": truncated, "partial_errors": partial_errors},
    }
    if selected_tool_name is not None:
        artifact["tool_name"] = selected_tool_name
    artifact["tool_call_id"] = tool_call_id
    return artifact


def extract_chat_artifact(content: Any, tool_name: str | None = None, tool_call_id: str | None = None) -> dict | None:
    for candidate in _artifact_candidates(content):
        if isinstance(candidate, dict) and candidate.get("type") == "text":
            candidate = candidate.get("text")
        if isinstance(candidate, str):
            if len(candidate.encode("utf-8")) > _MAX_BYTES:
                continue
            try:
                candidate = json.loads(candidate)
            except (TypeError, ValueError):
                continue
        if not isinstance(candidate, dict):
            continue
        kind = candidate.get("kind")
        if kind == "procurement_opportunities" and candidate.get("schema_version") == "1.0":
            normalizer = _normalize_artifact
        elif kind == "contract_awards" and candidate.get("schema_version") == "1.0":
            normalizer = _normalize_contract_awards_artifact
        else:
            continue
        try:
            artifact = normalizer(candidate, tool_name, tool_call_id)
        except (TypeError, ValueError):
            continue
        if len(json.dumps(artifact, separators=(",", ":")).encode("utf-8")) <= _MAX_BYTES:
            return artifact
    return None


def extract_chat_artifact_source_urls(content: Any) -> list[str]:
    """Return sanitized canonical links only from a validated artifact."""
    artifact = extract_chat_artifact(content)
    if not artifact:
        return []
    sources: list[str] = []
    for item in artifact["items"]:
        if artifact["kind"] == "contract_awards":
            url = item.get("usaspending_permalink")
        else:
            url = item.get("source", {}).get("url")
        if url and url not in sources:
            sources.append(url)
    return sources
