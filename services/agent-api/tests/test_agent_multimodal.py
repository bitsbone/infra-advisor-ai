"""Tests for the multimodal (image + audio) attachment wiring in agent.py.

Strategy
--------
* Unit-level: exercises _build_effective_query / _build_human_message
  directly rather than the full run_agent pipeline, since those two
  functions are where all the attachment-specific logic lives — the
  full pipeline (router, specialist executor, MCP tools) is already
  covered by test_agent_integration.py and is unaffected by this feature
  except for the message content it's handed.
* media.transcribe_audio is patched — no live Whisper deployment required.
"""

import contextlib
import os
import sys
from unittest.mock import MagicMock, patch

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
    """LLMObs.task() is a real span context manager backed by ddtrace's
    tracer; with DD_LLMOBS_ENABLED=false (this test env) a span never gets
    its "kind" set, so LLMObs.annotate(..., output_data=...) raises
    LLMObsAnnotateSpanError — a real ddtrace behavior, not a bug in this
    feature (production always runs with DD_LLMOBS_ENABLED=true). These
    tests care about the query-building logic, not LLMObs internals, so
    LLMObs itself is mocked out here the same way test_agent_integration.py
    mocks out the MCP/LLM/Redis clients it doesn't want to exercise for
    real.
    """
    yield MagicMock()


def _patch_llmobs():
    return (
        patch("agent.LLMObs.task", side_effect=_fake_llmobs_span),
        patch("agent.LLMObs.annotate"),
    )


def test_no_attachments_returns_query_unchanged():
    effective_query, image, tags = _build_effective_query("plain text query", None)
    assert effective_query == "plain text query"
    assert image is None
    assert tags["attachments.image_present"] == "false"
    assert tags["attachments.audio_present"] == "false"


def test_image_attachment_surfaced_without_transcription():
    effective_query, image, tags = _build_effective_query("what's in this photo?", [_IMAGE_ATTACHMENT])
    assert effective_query == "what's in this photo?"
    assert image == _IMAGE_ATTACHMENT
    assert tags["attachments.image_present"] == "true"
    assert tags["attachments.audio_present"] == "false"
    assert tags["image.blob_url"] == _IMAGE_ATTACHMENT["url"]


def test_audio_attachment_folds_transcript_into_query():
    task_patch, annotate_patch = _patch_llmobs()
    with task_patch, annotate_patch, patch(
        "agent.transcribe_audio", return_value=("what is the bridge condition", 1.23)
    ):
        effective_query, image, tags = _build_effective_query("", [_AUDIO_ATTACHMENT])

    assert effective_query == "what is the bridge condition"
    assert image is None
    assert tags["attachments.audio_present"] == "true"
    assert tags["audio.mime_type"] == "audio/webm"
    assert tags["audio.duration_s"] == "1.23"


def test_audio_transcript_appended_to_existing_text_query():
    task_patch, annotate_patch = _patch_llmobs()
    with task_patch, annotate_patch, patch(
        "agent.transcribe_audio", return_value=("also check the water system", 0.5)
    ):
        effective_query, _, _ = _build_effective_query("bridges near Austin", [_AUDIO_ATTACHMENT])

    assert "bridges near Austin" in effective_query
    assert "also check the water system" in effective_query


def test_audio_transcription_failure_falls_back_to_original_query():
    task_patch, annotate_patch = _patch_llmobs()
    with task_patch, annotate_patch, patch(
        "agent.transcribe_audio", side_effect=Exception("whisper unavailable")
    ):
        effective_query, image, tags = _build_effective_query("original query", [_AUDIO_ATTACHMENT])

    assert effective_query == "original query"
    assert tags["audio.duration_s"] == "0.00"


def test_both_image_and_audio_attachments_handled_together():
    task_patch, annotate_patch = _patch_llmobs()
    with task_patch, annotate_patch, patch(
        "agent.transcribe_audio", return_value=("describe this", 2.0)
    ):
        effective_query, image, tags = _build_effective_query(
            "", [_IMAGE_ATTACHMENT, _AUDIO_ATTACHMENT]
        )

    assert effective_query == "describe this"
    assert image == _IMAGE_ATTACHMENT
    assert tags["attachments.image_present"] == "true"
    assert tags["attachments.audio_present"] == "true"


def test_build_human_message_plain_text_without_image():
    msg = _build_human_message("plain query", None)
    assert msg.content == "plain query"


def test_build_human_message_multipart_with_image():
    msg = _build_human_message("what's in this photo?", _IMAGE_ATTACHMENT)
    assert isinstance(msg.content, list)
    assert msg.content[0] == {"type": "text", "text": "what's in this photo?"}
    assert msg.content[1] == {"type": "image_url", "image_url": {"url": _IMAGE_ATTACHMENT["url"]}}
