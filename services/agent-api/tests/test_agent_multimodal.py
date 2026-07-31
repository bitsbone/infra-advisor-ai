"""Tests for the multimodal (image + audio) attachment cascade in agent.py.

Strategy
--------
* Unit-level: exercises _build_effective_query / _build_human_message
  directly rather than the full run_agent pipeline, since those two
  functions are where all the attachment-specific logic lives — the full
  pipeline (router, specialist executor, MCP tools) is already covered by
  test_agent_integration.py and is unaffected by this feature except for
  the (always-text) message content it's handed.
* Both modalities cascade to plain text BEFORE the specialist agent runs:
  audio via a mocked media.transcribe_audio, images via a mocked
  AzureChatOpenAI.ainvoke (the "describe-image" vision call) — no live
  Whisper/Azure OpenAI deployment required.
* ddtrace.llmobs.LLMObs is mocked out (task/llm/annotate) since these are
  unit tests of the query-cascade logic, not of LLM Observability itself —
  same rationale as test_agent_integration.py mocking out MCP/LLM/Redis.
"""

import contextlib
import os
import sys
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

os.environ.setdefault("DD_AGENT_HOST", "localhost")
os.environ.setdefault("DD_TRACE_ENABLED", "false")
os.environ.setdefault("DD_LLMOBS_ENABLED", "false")
os.environ.setdefault("AZURE_OPENAI_ENDPOINT", "https://mock.openai.azure.com")
os.environ.setdefault("AZURE_OPENAI_API_KEY", "mock-key")
os.environ.setdefault("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini")
os.environ.setdefault("MCP_SERVER_URL", "http://mock-mcp:8000/mcp")
os.environ.setdefault("REDIS_HOST", "localhost")
os.environ.setdefault("AZURE_STORAGE_CONNECTION_STRING", "mock-connection-string")
os.environ.setdefault("AZURE_OPENAI_WHISPER_ENDPOINT", "https://mock-whisper.openai.azure.com")
os.environ.setdefault("AZURE_OPENAI_WHISPER_API_KEY", "mock-whisper-key")

_SRC = os.path.join(os.path.dirname(__file__), "..", "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

from agent import _build_effective_query, _build_human_message  # noqa: E402

_IMAGE_ATTACHMENT = {
    "url": "https://stinfraadvdev.blob.core.windows.net/chat-media/bridge.jpg?sig=x",
    "kind": "image",
    "mime_type": "image/jpeg",
    "size_bytes": 1234,
}
_AUDIO_ATTACHMENT = {
    "url": "https://stinfraadvdev.blob.core.windows.net/chat-media/voice.webm?sig=x",
    "kind": "audio",
    "mime_type": "audio/webm",
    "size_bytes": 5678,
}


@contextlib.contextmanager
def _fake_llmobs_span(*_args, **_kwargs):
    """LLMObs.llm()/task() are real span context managers backed by
    ddtrace's tracer; with DD_LLMOBS_ENABLED=false (this test env) a span
    never gets its "kind" set, so LLMObs.annotate(..., input_data=...)
    raises LLMObsAnnotateSpanError — a real ddtrace behavior, not a bug in
    this feature (production always runs with DD_LLMOBS_ENABLED=true).
    These tests care about the query-cascade logic, not LLMObs internals,
    so LLMObs itself is mocked out here the same way test_agent_integration.py
    mocks out the MCP/LLM/Redis clients it doesn't want to exercise for real.
    """
    yield MagicMock()


def _patch_llmobs():
    return (
        patch("agent.LLMObs.llm", side_effect=_fake_llmobs_span),
        patch("agent.LLMObs.task", side_effect=_fake_llmobs_span),
        patch("agent.LLMObs.annotate"),
    )


def _fake_llm(description: str = "A steel truss bridge with visible surface rust.") -> MagicMock:
    """Mock AzureChatOpenAI whose .ainvoke() returns a canned vision
    description and whose .deployment_name matches the real field LLMObs
    annotation reads for model_name."""
    llm = MagicMock()
    llm.deployment_name = "gpt-4.1-mini"
    response = MagicMock()
    response.content = description
    llm.ainvoke = AsyncMock(return_value=response)
    return llm


async def _run(query, attachments, llm=None):
    task_patch, llm_patch, annotate_patch = _patch_llmobs()
    with task_patch, llm_patch, annotate_patch, patch("agent.download_media_bytes", return_value=b"fake-image-bytes"):
        return await _build_effective_query(query, attachments, llm or _fake_llm())


async def test_no_attachments_returns_query_unchanged():
    effective_query, tags = await _run("plain text query", None)
    assert effective_query == "plain text query"
    assert tags["attachments.image_present"] == "false"
    assert tags["attachments.audio_present"] == "false"


async def test_image_attachment_folds_description_into_query():
    llm = _fake_llm("A steel truss bridge with visible surface rust.")
    effective_query, tags = await _run("what's in this photo?", [_IMAGE_ATTACHMENT], llm)

    assert "what's in this photo?" in effective_query
    assert "A steel truss bridge with visible surface rust." in effective_query
    assert tags["attachments.image_present"] == "true"
    assert tags["attachments.audio_present"] == "false"
    assert tags["image.blob_url"] == _IMAGE_ATTACHMENT["url"]
    llm.ainvoke.assert_awaited_once()


async def test_image_only_query_uses_description_as_effective_query():
    llm = _fake_llm("A cracked concrete overpass support column.")
    effective_query, _ = await _run("", [_IMAGE_ATTACHMENT], llm)
    assert "A cracked concrete overpass support column." in effective_query


async def test_audio_attachment_folds_transcript_into_query():
    with patch("agent.transcribe_audio", return_value=("what is the bridge condition", 1.23, b"fake-audio-bytes")):
        effective_query, tags = await _run("", [_AUDIO_ATTACHMENT])

    assert effective_query == "what is the bridge condition"
    assert tags["attachments.audio_present"] == "true"
    assert tags["audio.mime_type"] == "audio/webm"


async def test_audio_transcript_appended_to_existing_text_query():
    with patch("agent.transcribe_audio", return_value=("also check the water system", 0.5, b"fake-audio-bytes")):
        effective_query, _ = await _run("bridges near Austin", [_AUDIO_ATTACHMENT])

    assert "bridges near Austin" in effective_query
    assert "also check the water system" in effective_query


async def test_audio_transcription_failure_falls_back_to_original_query():
    with patch("agent.transcribe_audio", side_effect=Exception("whisper unavailable")):
        effective_query, _ = await _run("original query", [_AUDIO_ATTACHMENT])
    assert effective_query == "original query"


async def test_image_description_failure_falls_back_gracefully():
    llm = _fake_llm()
    llm.ainvoke = AsyncMock(side_effect=Exception("vision call failed"))
    effective_query, tags = await _run("original query", [_IMAGE_ATTACHMENT], llm)
    assert effective_query == "original query"
    assert tags["attachments.image_present"] == "true"


async def test_both_image_and_audio_attachments_handled_together():
    llm = _fake_llm("A flooded roadway with visible water damage.")
    with patch("agent.transcribe_audio", return_value=("describe this", 2.0, b"fake-audio-bytes")):
        effective_query, tags = await _run("", [_IMAGE_ATTACHMENT, _AUDIO_ATTACHMENT], llm)

    assert "describe this" in effective_query
    assert "A flooded roadway with visible water damage." in effective_query
    assert tags["attachments.image_present"] == "true"
    assert tags["attachments.audio_present"] == "true"


def test_build_human_message_is_always_plain_text():
    msg = _build_human_message("plain query")
    assert msg.content == "plain query"
