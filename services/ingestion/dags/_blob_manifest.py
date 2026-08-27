"""Small, versioned XCom references for record collections stored in Azure Blob.

Airflow's metadata database is not a bulk-data transport. These helpers keep
record payloads in Blob Storage and place only a credential-free manifest in
XCom. A manifest is safe to retry: the same ``run_id`` and blob path overwrite
the same object, while the checksum detects partial or unexpected content.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
from collections.abc import Mapping, Sequence
from typing import Any

SCHEMA_VERSION = "1.0"
JSON_LINES_CONTENT_TYPE = "application/x-ndjson"
JSON_LINES_ENCODING = "utf-8"


def build_run_blob_path(prefix: str, stem: str, run_id: str, suffix: str) -> str:
    """Build a retry-stable, collision-resistant path without leaking run syntax."""
    normalized_run_id = str(run_id)
    safe_run_id = re.sub(r"[^a-zA-Z0-9_-]+", "-", normalized_run_id).strip("-")
    safe_run_id = safe_run_id[:48] or "run"
    digest = hashlib.sha256(normalized_run_id.encode("utf-8")).hexdigest()[:12]
    return f"{prefix.strip('/')}/{stem}_{safe_run_id}_{digest}{suffix}"


def get_container_client(container_name: str):
    """Return the configured container, creating it only when it is absent."""
    from azure.core.exceptions import ResourceExistsError
    from azure.storage.blob import BlobServiceClient

    connection_string = os.environ["AZURE_STORAGE_CONNECTION_STRING"]
    service = BlobServiceClient.from_connection_string(connection_string)
    container = service.get_container_client(container_name)
    try:
        container.create_container()
    except ResourceExistsError:
        pass
    return container


def write_records_manifest(
    container_client,
    *,
    container_name: str,
    blob_path: str,
    records: Sequence[Mapping[str, Any]],
    source: str,
    run_id: str,
    dag_id: str,
) -> dict[str, Any]:
    """Write deterministic JSON Lines and return its credential-free manifest."""
    payload = b"".join(
        (
            json.dumps(
                dict(record),
                ensure_ascii=False,
                sort_keys=True,
                separators=(",", ":"),
                default=str,
            )
            + "\n"
        ).encode(JSON_LINES_ENCODING)
        for record in records
    )
    return write_blob_manifest(
        container_client,
        container_name=container_name,
        blob_path=blob_path,
        payload=payload,
        source=source,
        run_id=run_id,
        record_count=len(records),
        content_type=JSON_LINES_CONTENT_TYPE,
        content_encoding=JSON_LINES_ENCODING,
        dag_id=dag_id,
    )


def write_blob_manifest(
    container_client,
    *,
    container_name: str,
    blob_path: str,
    payload: bytes,
    source: str,
    run_id: str,
    record_count: int,
    content_type: str,
    content_encoding: str | None,
    dag_id: str,
) -> dict[str, Any]:
    """Write bytes and return a validated versioned manifest."""
    from _dd_blob import dd_upload_blob

    _validate_reference(container_name, blob_path)
    if record_count < 0:
        raise ValueError("record_count must not be negative")

    checksum = hashlib.sha256(payload).hexdigest()
    dd_upload_blob(container_client, blob_path, payload, dag_id=dag_id)
    manifest = {
        "schema_version": SCHEMA_VERSION,
        "source": source,
        "run_id": str(run_id),
        "blob": {"container": container_name, "path": blob_path},
        "record_count": record_count,
        "checksum": {"algorithm": "sha256", "value": checksum},
        "content_type": content_type,
        "content_encoding": content_encoding,
    }
    validate_manifest(manifest)
    return manifest


def read_records_manifest(
    container_client,
    manifest: Mapping[str, Any],
    *,
    expected_source: str | None = None,
) -> list[dict[str, Any]]:
    """Download, verify, and decode a JSON Lines record manifest."""
    validate_manifest(manifest, expected_source=expected_source)
    if manifest["content_type"] != JSON_LINES_CONTENT_TYPE:
        raise ValueError("manifest does not reference JSON Lines records")
    if manifest["content_encoding"] != JSON_LINES_ENCODING:
        raise ValueError("unsupported record manifest encoding")

    blob_path = manifest["blob"]["path"]
    payload = container_client.get_blob_client(blob_path).download_blob().readall()
    if not isinstance(payload, bytes):
        payload = bytes(payload)
    actual_checksum = hashlib.sha256(payload).hexdigest()
    expected_checksum = manifest["checksum"]["value"]
    if not hmac.compare_digest(actual_checksum, expected_checksum):
        raise ValueError("blob payload checksum does not match manifest")

    records = []
    for line_number, line in enumerate(payload.decode(JSON_LINES_ENCODING).splitlines(), 1):
        if not line:
            continue
        record = json.loads(line)
        if not isinstance(record, dict):
            raise ValueError(f"record at line {line_number} is not a JSON object")
        records.append(record)
    if len(records) != manifest["record_count"]:
        raise ValueError("blob record count does not match manifest")
    return records


def validate_manifest(
    manifest: Mapping[str, Any], *, expected_source: str | None = None
) -> None:
    """Reject incompatible, incomplete, or credential-bearing manifests."""
    required = {
        "schema_version",
        "source",
        "run_id",
        "blob",
        "record_count",
        "checksum",
        "content_type",
        "content_encoding",
    }
    missing = sorted(required - manifest.keys())
    if missing:
        raise ValueError(f"manifest is missing fields: {missing}")
    unexpected = sorted(manifest.keys() - required)
    if unexpected:
        raise ValueError(f"manifest has unsupported fields: {unexpected}")
    if manifest["schema_version"] != SCHEMA_VERSION:
        raise ValueError(f"unsupported manifest schema: {manifest['schema_version']!r}")
    for field in ("source", "run_id", "content_type"):
        if not isinstance(manifest[field], str) or not manifest[field]:
            raise ValueError(f"manifest {field} must be a non-empty string")
    if manifest["content_encoding"] is not None and not isinstance(
        manifest["content_encoding"], str
    ):
        raise ValueError("manifest content_encoding must be a string or null")
    if expected_source is not None and manifest["source"] != expected_source:
        raise ValueError("manifest source does not match the consuming task")
    if not isinstance(manifest["record_count"], int) or manifest["record_count"] < 0:
        raise ValueError("manifest record_count must be a non-negative integer")

    blob = manifest["blob"]
    if not isinstance(blob, Mapping):
        raise ValueError("manifest blob reference must be an object")
    container_name = blob.get("container")
    blob_path = blob.get("path")
    if not isinstance(container_name, str) or not isinstance(blob_path, str):
        raise ValueError("manifest blob container and path must be strings")
    _validate_reference(container_name, blob_path)

    checksum = manifest["checksum"]
    if not isinstance(checksum, Mapping) or checksum.get("algorithm") != "sha256":
        raise ValueError("manifest checksum must use sha256")
    checksum_value = checksum.get("value")
    if not isinstance(checksum_value, str) or re.fullmatch(r"[0-9a-f]{64}", checksum_value) is None:
        raise ValueError("manifest checksum value is not a SHA-256 digest")


def _validate_reference(container_name: str, blob_path: str) -> None:
    if not container_name or not blob_path:
        raise ValueError("blob container and path are required")
    if "?" in container_name or "?" in blob_path:
        raise ValueError("blob manifests must not contain URL query strings")
    if "://" in container_name or "://" in blob_path:
        raise ValueError("blob manifests must contain names, not URLs")
