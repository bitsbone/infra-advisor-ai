"""KAFKA_ENABLED turns off every Kafka touchpoint (dev namespace isolation)."""

import os
import sys
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

_SRC = os.path.join(os.path.dirname(__file__), "..", "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

import kafka_producer
from kafka_config import kafka_enabled


@pytest.mark.parametrize(
    ("value", "expected"),
    [(None, True), ("true", True), ("TRUE", True), ("false", False), ("False", False), ("0", False), (" off ", False)],
)
def test_kafka_enabled_parses_env(monkeypatch, value, expected):
    if value is None:
        monkeypatch.delenv("KAFKA_ENABLED", raising=False)
    else:
        monkeypatch.setenv("KAFKA_ENABLED", value)
    assert kafka_enabled() is expected


def _publish():
    kafka_producer.publish_contract_awards_event(
        session_id="s1", tool_call_id=None, query_input={}, raw_awards=[], deduped_award_count=0
    )


def test_producer_is_never_built_when_disabled(monkeypatch):
    monkeypatch.setenv("KAFKA_ENABLED", "false")
    monkeypatch.setattr(kafka_producer, "_producer", None)
    with patch("kafka_producer.Producer") as producer_cls:
        _publish()
    producer_cls.assert_not_called()


def test_producer_publishes_when_enabled(monkeypatch):
    monkeypatch.setenv("KAFKA_ENABLED", "true")
    monkeypatch.setattr(kafka_producer, "_producer", None)
    with patch("kafka_producer.Producer") as producer_cls:
        _publish()
    producer_cls.return_value.produce.assert_called_once()


@pytest.mark.parametrize(("value", "starts"), [("false", False), ("true", True)])
def test_lifespan_starts_consumer_only_when_enabled(monkeypatch, value, starts):
    from fastapi.testclient import TestClient

    monkeypatch.setenv("KAFKA_ENABLED", value)
    mock_mcp = MagicMock()
    mock_mcp.get_tools = AsyncMock(return_value=[])
    with (
        patch("main.build_mcp_client", return_value=mock_mcp),
        patch("main.build_llm", return_value=None),
        patch("main.enable_llm_obs"),
        patch("main.init_db"),
        patch("main.start_consumer_thread") as start_consumer,
    ):
        from main import app

        with TestClient(app):
            pass
    assert start_consumer.called is starts
