"""Privacy and structure contracts for the MCP JSON console formatter."""

import json
import logging
import os
import sys
from pathlib import Path

os.environ.setdefault("DD_TRACE_ENABLED", "false")

_SRC = Path(__file__).resolve().parents[1] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

from observability.json_logging import DatadogJsonFormatter, install_json_logging


def test_json_formatter_keeps_structured_fields_and_redacts_exception_message():
    sentinel = "PRIVATE-PROVIDER-PAYLOAD-DO-NOT-LOG"
    try:
        raise RuntimeError(sentinel)
    except RuntimeError:
        exc_info = sys.exc_info()

    record = logging.LogRecord(
        name="procurement",
        level=logging.ERROR,
        pathname=__file__,
        lineno=1,
        msg="Provider request failed",
        args=(),
        exc_info=exc_info,
    )
    record.__dict__.update(
        {
            "event": "procurement.provider.failed",
            "tool.name": "get_procurement_opportunities",
            "artifact.status": "partial",
        }
    )

    payload = json.loads(DatadogJsonFormatter().format(record))

    assert payload["event"] == "procurement.provider.failed"
    assert payload["tool.name"] == "get_procurement_opportunities"
    assert payload["artifact.status"] == "partial"
    assert payload["error.type"] == "RuntimeError"
    assert "test_json_logging.py" in payload["error.stack"]
    assert sentinel not in json.dumps(payload)


def test_installer_reformats_a_preconfigured_production_root_handler():
    root = logging.getLogger()
    original_handlers = root.handlers[:]
    original_level = root.level
    handler = logging.StreamHandler()
    handler.setFormatter(logging.Formatter("plain:%(message)s"))
    try:
        root.handlers[:] = [handler]
        install_json_logging()

        assert root.handlers == [handler]
        assert isinstance(handler.formatter, DatadogJsonFormatter)
        payload = json.loads(handler.format(logging.LogRecord("uvicorn", logging.INFO, __file__, 1, "ready", (), None)))
        assert payload["message"] == "ready"
    finally:
        root.handlers[:] = original_handlers
        root.setLevel(original_level)
