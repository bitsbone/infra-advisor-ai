"""Tenant and outbound attachment security boundaries."""

import os
import sys
import uuid

import pytest

_SRC = os.path.join(os.path.dirname(__file__), "..", "src")
if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

from media import InvalidAttachmentReference, validate_attachment_reference
from kafka_consumer import _synthetic_session_key
from tenant import tenant_session_key


def _attachment(
    *,
    host: str = "fieldmedia.blob.core.windows.net",
    container: str = "chat-media",
    path_kind: str = "image",
    kind: str = "image",
    mime_type: str = "image/jpeg",
    size_bytes: int = 1024,
) -> dict:
    blob_id = uuid.UUID("550e8400-e29b-41d4-a716-446655440000").hex
    return {
        "url": f"https://{host}/{container}/{path_kind}/{blob_id}?sv=2025-01-05&se=2026-09-01T00%3A00%3A00Z&sp=r&sr=b&sig=test-signature",
        "kind": kind,
        "mime_type": mime_type,
        "size_bytes": size_bytes,
    }


@pytest.fixture(autouse=True)
def storage_contract(monkeypatch):
    monkeypatch.setenv(
        "AZURE_STORAGE_CONNECTION_STRING",
        "AccountName=fieldmedia;EndpointSuffix=core.windows.net",
    )
    monkeypatch.setenv("AZURE_STORAGE_MEDIA_CONTAINER", "chat-media")
    monkeypatch.delenv("AZURE_STORAGE_BLOB_ENDPOINT", raising=False)


def test_tenant_session_key_is_stable_opaque_and_user_scoped():
    first = tenant_session_key("user-a", "shared-session")
    assert first == tenant_session_key("user-a", "shared-session")
    assert first != tenant_session_key("user-b", "shared-session")
    assert "user-a" not in first and "shared-session" not in first
    assert _synthetic_session_key("shared-session") not in {
        first,
        tenant_session_key("user-b", "shared-session"),
    }


def test_valid_service_issued_attachment_is_accepted():
    assert validate_attachment_reference(_attachment())["mime_type"] == "image/jpeg"


@pytest.mark.parametrize(
    "attachment",
    [
        _attachment(host="evil.example"),
        _attachment(container="other-container"),
        _attachment(path_kind="audio"),
        _attachment(kind="image", mime_type="audio/webm"),
        _attachment(size_bytes=0),
        _attachment(size_bytes=10 * 1024 * 1024 + 1),
    ],
)
def test_attachment_rejects_foreign_container_and_metadata_mismatches(attachment):
    with pytest.raises(InvalidAttachmentReference):
        validate_attachment_reference(attachment)


@pytest.mark.parametrize("host", ["127.0.0.1", "10.0.0.7", "169.254.169.254", "[::1]"])
def test_attachment_rejects_loopback_private_and_link_local_hosts(monkeypatch, host):
    monkeypatch.setenv("AZURE_STORAGE_BLOB_ENDPOINT", f"https://{host}")
    with pytest.raises(InvalidAttachmentReference):
        validate_attachment_reference(_attachment(host=host))


def test_attachment_rejects_non_read_only_or_incomplete_sas():
    attachment = _attachment()
    attachment["url"] = attachment["url"].replace("sp=r", "sp=rw")
    with pytest.raises(InvalidAttachmentReference):
        validate_attachment_reference(attachment)

    attachment = _attachment()
    attachment["url"] = attachment["url"].replace("&sig=test-signature", "")
    with pytest.raises(InvalidAttachmentReference):
        validate_attachment_reference(attachment)
