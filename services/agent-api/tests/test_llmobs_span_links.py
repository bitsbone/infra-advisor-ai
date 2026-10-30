"""link_from must put output → input span links on the finished span event.

The Agent Observability execution graph draws an edge between sibling spans
only from these links, and link_from relies on ddtrace's private
LLMObs.annotate(_linked_spans=...). This test runs LLMObs for real and
captures each span event as ddtrace builds it; agent mode (no API key) keeps
any flush on localhost, never Datadog's public intake. A ddtrace upgrade that
changes the private parameter fails here instead of silently flattening the
graph.
"""

import sys
from pathlib import Path

import pytest
from ddtrace.llmobs import LLMObs

sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

from observability.llm_obs import link_from  # noqa: E402


@pytest.fixture
def span_events(monkeypatch):
    events: list = []
    build_event = LLMObs._llmobs_span_event

    def capture(self, span):
        event = build_event(self, span)
        events.append(event)
        return event

    monkeypatch.setattr(LLMObs, "_llmobs_span_event", capture)
    # Other test modules set DD_LLMOBS_ENABLED=false at import, which makes
    # enable() a silent no-op.
    monkeypatch.setenv("DD_LLMOBS_ENABLED", "true")
    LLMObs.enable(ml_app="test-span-links", agentless_enabled=False)
    yield events
    LLMObs.disable()


def test_link_from_records_output_to_input_links(span_events):
    with LLMObs.workflow("query-processing"):
        with LLMObs.task("load-history") as history:
            pass
        with LLMObs.agent("router") as router:
            pass
        with LLMObs.agent("specialist-general") as specialist:
            link_from(specialist, router, history)

    by_name = {event["name"]: event for event in span_events}
    links = by_name["specialist-general"]["span_links"]
    assert [(link["span_id"], link["attributes"]) for link in links] == [
        (str(router.span_id), {"from": "output", "to": "input"}),
        (str(history.span_id), {"from": "output", "to": "input"}),
    ]
    assert all(link["trace_id"] == by_name["router"]["trace_id"] for link in links)
    assert "span_links" not in by_name["router"]
