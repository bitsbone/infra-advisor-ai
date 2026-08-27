"""Datadog-compatible JSON console logging with a strict telemetry allowlist.

Uvicorn installs logging handlers before it imports an application module. A
plain ``logging.basicConfig`` call is therefore a no-op in production. The
installer below updates existing console handlers and creates one only when a
process has not configured logging yet.
"""

import json
import logging
import os
import traceback
from datetime import datetime, timezone


class DatadogJsonFormatter(logging.Formatter):
    """Emit one JSON object per line with deliberately bounded fields."""

    _structured_fields = (
        "event",
        "tool.name",
        "artifact.kind",
        "artifact.schema_version",
        "artifact.status",
        "artifact.returned_count",
        "artifact.provider_counts",
        "artifact.truncated",
        "artifact.partial_error_count",
        "artifact.sample",
        "duration_ms",
    )

    def format(self, record: logging.LogRecord) -> str:
        payload = {
            "timestamp": datetime.fromtimestamp(record.created, timezone.utc).isoformat(),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
            "dd.service": record.__dict__.get("dd.service", ""),
            "dd.env": record.__dict__.get("dd.env", ""),
            "dd.version": record.__dict__.get("dd.version", ""),
            "dd.trace_id": record.__dict__.get("dd.trace_id", "0"),
            "dd.span_id": record.__dict__.get("dd.span_id", "0"),
        }
        for field in self._structured_fields:
            if field in record.__dict__:
                payload[field] = record.__dict__[field]
        if record.exc_info:
            # Preserve symbolizable frames, but omit exception prose because it
            # can include provider URLs, response fragments, or credentials.
            exc_type, _, exc_traceback = record.exc_info
            payload["error.type"] = exc_type.__name__ if exc_type else "Exception"
            payload["error.stack"] = "".join(traceback.format_list(traceback.extract_tb(exc_traceback)))
        return json.dumps(payload, separators=(",", ":"), default=str)


def install_json_logging() -> None:
    """Apply JSON formatting to root and Uvicorn console handlers.

    The operation is idempotent so application factories and test imports can
    call it safely more than once.
    """

    formatter = DatadogJsonFormatter()
    level = logging._nameToLevel.get(os.environ.get("LOG_LEVEL", "INFO").upper(), logging.INFO)
    root = logging.getLogger()
    root.setLevel(level)

    stream_handlers = [handler for handler in root.handlers if isinstance(handler, logging.StreamHandler)]
    if not stream_handlers:
        stream_handlers = [logging.StreamHandler()]
        root.addHandler(stream_handlers[0])
    for handler in stream_handlers:
        handler.setFormatter(formatter)

    # Uvicorn loggers normally have their own non-propagating handlers. They
    # already exist when ``uvicorn main:app`` imports this module.
    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        for handler in logging.getLogger(name).handlers:
            if isinstance(handler, logging.StreamHandler):
                handler.setFormatter(formatter)
