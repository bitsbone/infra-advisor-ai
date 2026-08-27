"""Tests for POST /media/upload (main.py) and the media.upload_media helper.

Strategy
--------
* Blob Storage is fully mocked — no live Azure account required.
* Uses the same client fixture pattern as test_agent_integration.py (MCP/LLM/
  Redis mocked, JWT auth header pre-set).

Coverage
--------
* Successful image upload returns {url, kind, mime_type, size_bytes}
* Successful audio upload returns kind="audio"
* Unsupported content-type -> 415
* Oversized file -> 413
* Missing auth -> 401
"""

import os
import sys
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from fastapi.testclient import TestClient

os.environ.setdefault("DD_AGENT_HOST", "localhost")
os.environ.setdefault("DD_TRACE_ENABLED", "false")
os.environ.setdefault("DD_LLMOBS_ENABLED", "false")
os.environ.setdefault("AZURE_OPENAI_ENDPOINT", "https://mock.openai.azure.com")
os.environ.setdefault("AZURE_OPENAI_API_KEY", "mock-key")
os.environ.setdefault("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini")
os.environ.setdefault("MCP_SERVER_URL", "http://mock-mcp:8000/mcp")
os.environ.setdefault("REDIS_HOST", "localhost")
os.environ.setdefault("JWT_SECRET", "test-secret-for-agent-api-unit-tests")
os.environ.setdefault("AZURE_STORAGE_CONNECTION_STRING", "mock-connection-string")
os.environ.setdefault("AZURE_OPENAI_WHISPER_ENDPOINT", "https://mock-whisper.openai.azure.com")
os.environ.setdefault("AZURE_OPENAI_WHISPER_API_KEY", "mock-whisper-key")
os.environ.setdefault("AZURE_STORAGE_MEDIA_CONTAINER", "chat-media")

_SRC = os.path.join(os.path.dirname(__file__), "..", "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)


def _make_test_token() -> str:
    from datetime import datetime, timedelta, timezone

    from jose import jwt
    return jwt.encode(
        {
            "sub": "test-user-id",
            "email": "tester@datadoghq.com",
            "exp": datetime.now(timezone.utc) + timedelta(hours=1),
        },
        os.environ["JWT_SECRET"],
        algorithm="HS256",
    )


_TEST_AUTH_HEADER = {"Authorization": f"Bearer {_make_test_token()}"}


class _FakeTool:
    name = "get_bridge_condition"
    description = "Query FHWA NBI"

    async def ainvoke(self, _):
        return []


@pytest.fixture()
def client():
    mock_mcp = MagicMock()
    mock_mcp.get_tools = AsyncMock(return_value=[_FakeTool()])
    mock_llm = MagicMock()

    with (
        patch("main.build_mcp_client", return_value=mock_mcp),
        patch("main.build_llm", return_value=mock_llm),
        patch("main.enable_llm_obs"),
        patch("main.start_consumer_thread"),
        patch("main._pool_maintenance_loop", new=AsyncMock(return_value=None)),
        patch("main._mcp_connected", True, create=True),
        patch("main._llm_connected", True, create=True),
    ):
        from main import app

        with TestClient(app, raise_server_exceptions=True, headers=_TEST_AUTH_HEADER) as c:
            import main as _main

            _main._mcp_client = mock_mcp
            _main._llm = mock_llm
            _main._mcp_connected = True
            _main._llm_connected = True
            yield c


def test_media_upload_image_returns_attachment(client):
    from media import MediaAttachment

    canned = MediaAttachment(
        url="https://stinfraadvdev.blob.core.windows.net/chat-media/abc.jpg?sig=x",
        kind="image",
        mime_type="image/jpeg",
        size_bytes=12,
    )
    with patch("main.upload_media", return_value=canned):
        resp = client.post(
            "/media/upload",
            files={"file": ("photo.jpg", b"fake-image-bytes", "image/jpeg")},
        )
    assert resp.status_code == 200
    body = resp.json()
    assert body["kind"] == "image"
    assert body["mime_type"] == "image/jpeg"
    assert body["url"].startswith("https://")


def test_media_upload_audio_returns_attachment(client):
    from media import MediaAttachment

    canned = MediaAttachment(
        url="https://stinfraadvdev.blob.core.windows.net/chat-media/voice.webm?sig=x",
        kind="audio",
        mime_type="audio/webm",
        size_bytes=99,
    )
    with patch("main.upload_media", return_value=canned):
        resp = client.post(
            "/media/upload",
            files={"file": ("voice.webm", b"fake-audio-bytes", "audio/webm")},
        )
    assert resp.status_code == 200
    assert resp.json()["kind"] == "audio"


def test_media_upload_rejects_unsupported_content_type(client):
    from media import UnsupportedMediaType

    with patch("main.upload_media", side_effect=UnsupportedMediaType("application/pdf")):
        resp = client.post(
            "/media/upload",
            files={"file": ("doc.pdf", b"fake-pdf-bytes", "application/pdf")},
        )
    assert resp.status_code == 415


def test_media_upload_rejects_oversized_file(client):
    from media import MediaTooLarge

    # Distinct JWT subject so this test's request doesn't share a rate-limit
    # bucket with the other /media/upload tests in this module — the
    # slowapi Limiter is a module-level singleton that persists for the
    # whole pytest session, so reusing _TEST_AUTH_HEADER here could 429
    # depending on how many prior tests already hit this endpoint as the
    # same user.
    import time

    from jose import jwt

    token = jwt.encode(
        {"sub": "test-user-oversized", "exp": int(time.time()) + 3600},
        os.environ["JWT_SECRET"],
        algorithm="HS256",
    )
    with patch("main.upload_media", side_effect=MediaTooLarge(20_000_000)):
        resp = client.post(
            "/media/upload",
            files={"file": ("big.jpg", b"x", "image/jpeg")},
            headers={"Authorization": f"Bearer {token}"},
        )
    assert resp.status_code == 413


def test_media_upload_requires_auth():
    """No Authorization header at all — a fresh TestClient without the
    default auth header from the `client` fixture."""
    mock_mcp = MagicMock()
    mock_mcp.get_tools = AsyncMock(return_value=[])
    mock_llm = MagicMock()

    with (
        patch("main.build_mcp_client", return_value=mock_mcp),
        patch("main.build_llm", return_value=mock_llm),
        patch("main.enable_llm_obs"),
        patch("main.start_consumer_thread"),
        patch("main._pool_maintenance_loop", new=AsyncMock(return_value=None)),
    ):
        if "main" in sys.modules:
            del sys.modules["main"]
        from main import app

        with TestClient(app, raise_server_exceptions=False) as c:
            resp = c.post(
                "/media/upload",
                files={"file": ("photo.jpg", b"fake-image-bytes", "image/jpeg")},
            )
        assert resp.status_code == 401


def test_blob_name_and_upload_span_exclude_filename_session_and_sas_query():
    """Regression: provider credentials and user identifiers stay out of storage IDs/telemetry."""
    from media import upload_media

    sentinel_filename = "PRIVATE-FILENAME-DO-NOT-TRACE.jpg"
    sentinel_session = "PRIVATE-SESSION-DO-NOT-TRACE"
    sentinel_sas = "sig=PRIVATE-SAS-DO-NOT-TRACE"

    service_client = MagicMock()
    service_client.account_name = "testaccount"
    service_client.credential.account_key = "test-key"
    blob_client = MagicMock()
    blob_client.url = "https://testaccount.blob.core.windows.net/chat-media/generated"
    container_client = MagicMock()
    container_client.upload_blob.return_value = blob_client
    service_client.get_container_client.return_value = container_client

    span = MagicMock()
    span_context = MagicMock()
    span_context.__enter__.return_value = span
    span_context.__exit__.return_value = False

    with (
        patch("media.get_blob_service_client", return_value=service_client),
        patch("media.generate_blob_sas", return_value=sentinel_sas),
        patch("media.tracer.trace", return_value=span_context),
    ):
        attachment = upload_media(
            b"safe-test-bytes",
            sentinel_filename,
            "image/jpeg;private=PRIVATE-CONTENT-TYPE-PARAM",
            sentinel_session,
        )

    blob_name = container_client.upload_blob.call_args.kwargs["name"]
    assert blob_name.startswith("image/")
    assert sentinel_filename not in blob_name
    assert sentinel_session not in blob_name
    assert "PRIVATE-CONTENT-TYPE-PARAM" not in repr(span.set_tag.call_args_list)
    assert sentinel_filename not in repr(span.set_tag.call_args_list)
    assert sentinel_session not in repr(span.set_tag.call_args_list)
    assert sentinel_sas not in repr(span.set_tag.call_args_list)
    assert attachment.url.endswith(sentinel_sas)  # still returned to the provider workflow
