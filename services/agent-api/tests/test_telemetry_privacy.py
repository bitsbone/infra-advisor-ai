"""Fail-closed contracts for custom agent telemetry."""

import os
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

os.environ.setdefault("DD_TRACE_ENABLED", "false")
os.environ.setdefault("DD_LLMOBS_ENABLED", "false")

_SRC = Path(__file__).resolve().parents[1] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))


def test_agent_annotation_uses_only_bounded_metadata():
    from observability.llm_obs import tag_agent_run

    sentinel_prompt = "PRIVATE-PROMPT-DO-NOT-TRACE"
    sentinel_answer = "PRIVATE-ANSWER-DO-NOT-TRACE"
    annotate = MagicMock()

    with patch("observability.llm_obs.LLMObs.annotate", annotate):
        tag_agent_run(
            span=MagicMock(),
            query=sentinel_prompt,
            answer=sentinel_answer,
            query_domain="engineering",
            tools_called=["get_bridge_condition"],
        )

    exported = repr(annotate.call_args_list)
    assert sentinel_prompt not in exported
    assert sentinel_answer not in exported
    assert "query.characters" in exported
    assert "response.characters" in exported


def test_custom_agent_sources_have_no_sensitive_attribute_keys():
    sources = (
        (_SRC / "agent.py").read_text(),
        (_SRC / "observability" / "llm_obs.py").read_text(),
    )
    combined = "\n".join(sources)

    for forbidden in (
        '"session.id"',
        '"session.chat_id"',
        '"session.rum_id"',
        '"audio.blob_url"',
        '"image.blob_url"',
        "input_data=",
        "output_data=",
    ):
        assert forbidden not in combined


def test_end_user_feedback_has_submitter_and_exactly_one_target():
    from observability.llm_obs import _feedback_payload

    metric = _feedback_payload("456", "positive", "user-123")["data"]["attributes"]["metrics"][0]

    assert metric["event_kind"] == "feedback"
    assert metric["span_id"] == "456"
    assert metric["submitter"] == {"id": "user-123", "type": "user"}
    assert metric["assessment"] == "pass"
    assert "join_on" not in metric
    assert "trace_id" not in metric
    assert "session_id" not in metric
