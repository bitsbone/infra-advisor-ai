"""Chat attachment storage + audio transcription for multimodal input.

Uploaded images/audio land in Azure Blob Storage (container name from
AZURE_STORAGE_MEDIA_CONTAINER); callers get back a read-only SAS URL rather
than raw bytes, so attachments can flow through the request body, Redis
conversation history, and LangChain vision content without bloating any of
them — see docs/agent-guides/core-conventions.md's Redis session-memory note.

Audio is never sent to the chat LLM directly (cascade architecture): it is
transcribed here via an Azure OpenAI Whisper deployment, and the transcript
text is what agent.py folds into the effective query.
"""

import logging
import mimetypes
import os
import time
import uuid
from datetime import datetime, timedelta, timezone
from typing import Literal

import httpx
from azure.storage.blob import (
    BlobSasPermissions,
    BlobServiceClient,
    ContentSettings,
    generate_blob_sas,
)
from ddtrace import tracer
from openai import AzureOpenAI
from pydantic import BaseModel

logger = logging.getLogger(__name__)

_ALLOWED_CONTENT_TYPES = {
    "image/jpeg": "image",
    "image/png": "image",
    "image/webp": "image",
    "audio/webm": "audio",
    "audio/wav": "audio",
    "audio/mpeg": "audio",
    "audio/ogg": "audio",
}
MAX_UPLOAD_BYTES = 10 * 1024 * 1024  # 10 MB


class UnsupportedMediaType(Exception):
    """Raised when the uploaded content-type is not on the allowlist."""


class MediaTooLarge(Exception):
    """Raised when the uploaded file exceeds MAX_UPLOAD_BYTES."""


class MediaAttachment(BaseModel):
    url: str
    kind: Literal["image", "audio"]
    mime_type: str
    size_bytes: int


def _media_container_name() -> str:
    return os.environ.get("AZURE_STORAGE_MEDIA_CONTAINER", "chat-media")


def _sas_expiry_hours() -> int:
    return int(os.environ.get("MEDIA_SAS_EXPIRY_HOURS", "168"))


def get_blob_service_client() -> BlobServiceClient:
    conn_str = os.environ["AZURE_STORAGE_CONNECTION_STRING"]
    return BlobServiceClient.from_connection_string(conn_str)


def _safe_filename(filename: str) -> str:
    # Strip any path components a client might smuggle in; keep the extension.
    base = os.path.basename(filename or "attachment")
    return base.replace("/", "_").replace("\\", "_") or "attachment"


def upload_media(
    file_bytes: bytes,
    filename: str,
    content_type: str,
    session_id: str,
) -> MediaAttachment:
    """Upload an image/audio file to Blob Storage and return a read-SAS URL.

    Raises UnsupportedMediaType / MediaTooLarge — callers translate these to
    415 / 413 HTTP responses.
    """
    # Browsers (MediaRecorder in particular) often send params after the type,
    # e.g. "audio/webm;codecs=opus" — match on the bare mime type.
    bare_content_type = content_type.split(";", 1)[0].strip().lower()
    kind = _ALLOWED_CONTENT_TYPES.get(bare_content_type)
    if kind is None:
        raise UnsupportedMediaType(content_type)
    if len(file_bytes) > MAX_UPLOAD_BYTES:
        raise MediaTooLarge(len(file_bytes))

    container_name = _media_container_name()
    blob_name = f"{session_id}/{uuid.uuid4()}-{_safe_filename(filename)}"

    with tracer.trace("azure.blob.upload", service="agent-api", resource=container_name) as span:
        span.set_tag("blob.container", container_name)
        span.set_tag("blob.name", blob_name)
        span.set_tag("blob.size_bytes", len(file_bytes))
        span.set_tag("blob.content_type", content_type)

        service_client = get_blob_service_client()
        container_client = service_client.get_container_client(container_name)
        blob_client = container_client.upload_blob(
            name=blob_name,
            data=file_bytes,
            content_settings=ContentSettings(content_type=bare_content_type),
            overwrite=True,
        )

        account_name = service_client.account_name
        account_key = service_client.credential.account_key
        expiry = datetime.now(timezone.utc) + timedelta(hours=_sas_expiry_hours())
        sas_token = generate_blob_sas(
            account_name=account_name,
            container_name=container_name,
            blob_name=blob_name,
            account_key=account_key,
            permission=BlobSasPermissions(read=True),
            expiry=expiry,
        )
        url = f"{blob_client.url}?{sas_token}"

    return MediaAttachment(
        url=url,
        kind=kind,
        mime_type=bare_content_type,
        size_bytes=len(file_bytes),
    )


def _whisper_client() -> AzureOpenAI:
    # Whisper lives on a SEPARATE Cognitive Services account/region from the
    # main chat/embedding deployments — whisper-001's "Standard" SKU isn't
    # offered in every region (confirmed absent in eastus via the Cognitive
    # Services models API), so it has its own account in a region that does
    # support it. See infra/bicep/modules/azure-openai.bicep.
    return AzureOpenAI(
        azure_endpoint=os.environ["AZURE_OPENAI_WHISPER_ENDPOINT"],
        api_key=os.environ["AZURE_OPENAI_WHISPER_API_KEY"],
        api_version="2025-01-01-preview",
    )


def transcribe_audio(url: str, mime_type: str) -> tuple[str, float]:
    """Download the SAS-URL'd audio blob and transcribe it via Azure OpenAI Whisper.

    Returns (transcript, duration_s). duration_s is a best-effort wall-clock
    measurement of the transcription call itself, not the audio's actual
    playback length — Whisper's response doesn't reliably include duration
    for every input format, and decoding the audio just to measure it isn't
    worth a new dependency for a demo feature.
    """
    deployment = os.environ.get("AZURE_OPENAI_WHISPER_DEPLOYMENT", "whisper")
    ext = mimetypes.guess_extension(mime_type) or ".webm"

    resp = httpx.get(url, timeout=30.0)
    resp.raise_for_status()
    audio_bytes = resp.content

    client = _whisper_client()
    start = time.monotonic()
    transcription = client.audio.transcriptions.create(
        model=deployment,
        file=(f"audio{ext}", audio_bytes, mime_type),
    )
    duration_s = time.monotonic() - start

    return transcription.text, duration_s
