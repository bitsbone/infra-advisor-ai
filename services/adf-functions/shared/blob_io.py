"""Direct Azure Blob read/write for pipeline hand-offs between activities.

No manifest/checksum layer here (unlike the old Airflow _blob_manifest.py) —
that existed solely to work around Airflow XCom's metadata-DB size limits.
ADF Function Activity outputs can carry a plain blob path as a pipeline
parameter with no equivalent size constraint, so activities just pass a
path string and the next activity reads it directly.
"""

import io
import json
import logging
import os

from azure.storage.blob import BlobServiceClient, ContainerClient

logger = logging.getLogger(__name__)

RAW_CONTAINER = "raw-data"
PREPARED_CONTAINER = "prepared-data"

_blob_service: BlobServiceClient | None = None


def _get_blob_service() -> BlobServiceClient:
    global _blob_service
    if _blob_service is None:
        conn_str = os.environ["AZURE_STORAGE_CONNECTION_STRING"]
        _blob_service = BlobServiceClient.from_connection_string(conn_str)
    return _blob_service


def get_container_client(container_name: str) -> ContainerClient:
    service = _get_blob_service()
    container = service.get_container_client(container_name)
    if not container.exists():
        container.create_container()
    return container


def write_json_records(container_name: str, blob_path: str, records: list[dict]) -> None:
    """Write records as JSON Lines (one JSON object per line)."""
    container = get_container_client(container_name)
    body = "\n".join(json.dumps(record, sort_keys=True) for record in records)
    container.upload_blob(blob_path, body.encode("utf-8"), overwrite=True)
    logger.info("Wrote %d records to %s/%s", len(records), container_name, blob_path)


def read_json_records(container_name: str, blob_path: str) -> list[dict]:
    container = get_container_client(container_name)
    downloaded = container.download_blob(blob_path).readall()
    text = downloaded.decode("utf-8")
    return [json.loads(line) for line in text.splitlines() if line.strip()]


def write_parquet_records(container_name: str, blob_path: str, records: list[dict]) -> None:
    """Archival snapshot — mirrors the old DAGs' raw Parquet storage step."""
    import pandas as pd

    container = get_container_client(container_name)
    buf = io.BytesIO()
    pd.DataFrame(records).to_parquet(buf, index=False)
    buf.seek(0)
    container.upload_blob(blob_path, buf.getvalue(), overwrite=True)
    logger.info("Wrote Parquet archive (%d records) to %s/%s", len(records), container_name, blob_path)
