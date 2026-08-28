"""Capacity controls that keep background LLM work from crowding out chat."""

import asyncio
import os
import sys
from unittest.mock import patch

import httpx
from openai import RateLimitError

_SRC = os.path.join(os.path.dirname(__file__), "..", "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

from agent import _bounded_retry_count, _stream_error_event, build_llm
from observability.llm_obs import schedule_faithfulness_score


def test_retry_count_is_bounded_and_invalid_values_are_safe():
    assert _bounded_retry_count("7") == 7
    assert _bounded_retry_count("-1") == 0
    assert _bounded_retry_count("99") == 10
    assert _bounded_retry_count("invalid") == 5


def test_llm_factory_applies_configured_retry_count():
    with (
        patch.dict(
            os.environ,
            {
                "AZURE_OPENAI_ENDPOINT": "https://mock.openai.azure.com",
                "AZURE_OPENAI_API_KEY": "test-key",
                "AZURE_OPENAI_MAX_RETRIES": "7",
            },
        ),
        patch("agent.AzureChatOpenAI") as llm_type,
    ):
        build_llm("gpt-test")

    assert llm_type.call_args.kwargs["max_retries"] == 7


def test_rate_limit_has_retryable_stream_contract():
    response = httpx.Response(
        429,
        request=httpx.Request("POST", "https://mock.openai.azure.com/chat"),
    )
    error = RateLimitError("quota exceeded", response=response, body=None)

    event = _stream_error_event(error)

    assert event["event"] == "error"
    assert event["category"] == "rate_limited"
    assert "quota" not in event["message"]


def test_faithfulness_zero_sample_rate_does_not_schedule():
    with (
        patch.dict(os.environ, {"EVAL_SAMPLE_RATE": "0"}),
        patch.object(asyncio, "get_event_loop") as get_loop,
    ):
        schedule_faithfulness_score("query", ["context"], "answer")

    get_loop.assert_not_called()


def test_faithfulness_full_sample_rate_schedules():
    loop = asyncio.new_event_loop()
    try:
        with (
            patch.dict(os.environ, {"EVAL_SAMPLE_RATE": "1"}),
            patch.object(asyncio, "get_event_loop", return_value=loop),
            patch.object(loop, "create_task") as create_task,
        ):
            schedule_faithfulness_score("query", ["context"], "answer")

        create_task.assert_called_once()
        create_task.call_args.args[0].close()
    finally:
        loop.close()
