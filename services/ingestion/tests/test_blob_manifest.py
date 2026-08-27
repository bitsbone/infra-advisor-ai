from __future__ import annotations

import importlib
import json
from pathlib import Path

import pytest


DAGS_DIR = Path(__file__).resolve().parents[1] / "dags"


class _Download:
    def __init__(self, payload: bytes):
        self.payload = payload

    def readall(self) -> bytes:
        return self.payload


class _Blob:
    def __init__(self, storage: dict[str, bytes], path: str):
        self.storage = storage
        self.path = path

    def upload_blob(self, data, *, overwrite: bool):
        assert overwrite is True
        self.storage[self.path] = data.read() if hasattr(data, "read") else bytes(data)

    def download_blob(self) -> _Download:
        return _Download(self.storage[self.path])


class _Container:
    def __init__(self):
        self.container_name = "raw-data"
        self.storage: dict[str, bytes] = {}

    def get_blob_client(self, path: str) -> _Blob:
        return _Blob(self.storage, path)


@pytest.fixture
def manifest_module(monkeypatch):
    monkeypatch.syspath_prepend(str(DAGS_DIR))
    return importlib.import_module("_blob_manifest")


def test_records_round_trip_through_small_versioned_manifest(manifest_module):
    container = _Container()
    records = [{"id": 2, "name": "bridge"}, {"id": 1, "name": "water"}]

    manifest = manifest_module.write_records_manifest(
        container,
        container_name="raw-data",
        blob_path="nbi/manifests/run.jsonl",
        records=records,
        source="fhwa.nbi",
        run_id="scheduled__2026-08-26T00:00:00+00:00",
        dag_id="nbi_refresh",
    )

    assert manifest == {
        "schema_version": "1.0",
        "source": "fhwa.nbi",
        "run_id": "scheduled__2026-08-26T00:00:00+00:00",
        "blob": {"container": "raw-data", "path": "nbi/manifests/run.jsonl"},
        "record_count": 2,
        "checksum": {
            "algorithm": "sha256",
            "value": manifest["checksum"]["value"],
        },
        "content_type": "application/x-ndjson",
        "content_encoding": "utf-8",
    }
    assert len(json.dumps(manifest)) < 1024
    assert manifest_module.read_records_manifest(
        container, manifest, expected_source="fhwa.nbi"
    ) == records


def test_blob_path_is_retry_stable_and_separates_runs(manifest_module):
    first = manifest_module.build_run_blob_path("fema", "records", "run-a", ".jsonl")
    retry = manifest_module.build_run_blob_path("fema", "records", "run-a", ".jsonl")
    other = manifest_module.build_run_blob_path("fema", "records", "run-b", ".jsonl")

    assert first == retry
    assert first != other
    assert "?" not in first
    assert "://" not in first


@pytest.mark.parametrize(
    ("container_name", "blob_path"),
    [
        ("raw-data?sig=secret", "records.jsonl"),
        ("raw-data", "records.jsonl?sig=secret"),
        ("https://account.blob.core.windows.net", "records.jsonl"),
    ],
)
def test_manifest_rejects_urls_and_query_strings(
    manifest_module, container_name, blob_path
):
    with pytest.raises(ValueError):
        manifest_module.write_records_manifest(
            _Container(),
            container_name=container_name,
            blob_path=blob_path,
            records=[],
            source="test",
            run_id="run",
            dag_id="test",
        )


def test_read_rejects_tampered_payload(manifest_module):
    container = _Container()
    manifest = manifest_module.write_records_manifest(
        container,
        container_name="raw-data",
        blob_path="records.jsonl",
        records=[{"id": 1}],
        source="test",
        run_id="run",
        dag_id="test",
    )
    container.storage["records.jsonl"] = b'{"id":2}\n'

    with pytest.raises(ValueError, match="checksum"):
        manifest_module.read_records_manifest(container, manifest)


def test_read_rejects_wrong_source_before_download(manifest_module):
    container = _Container()
    manifest = manifest_module.write_records_manifest(
        container,
        container_name="raw-data",
        blob_path="records.jsonl",
        records=[{"id": 1}],
        source="source-a",
        run_id="run",
        dag_id="test",
    )

    with pytest.raises(ValueError, match="source"):
        manifest_module.read_records_manifest(
            container, manifest, expected_source="source-b"
        )


def test_read_rejects_record_count_mismatch(manifest_module):
    container = _Container()
    manifest = manifest_module.write_records_manifest(
        container,
        container_name="raw-data",
        blob_path="records.jsonl",
        records=[{"id": 1}],
        source="test",
        run_id="run",
        dag_id="test",
    )
    manifest["record_count"] = 2

    with pytest.raises(ValueError, match="record count"):
        manifest_module.read_records_manifest(container, manifest)


def test_manifest_rejects_extra_url_fields(manifest_module):
    container = _Container()
    manifest = manifest_module.write_records_manifest(
        container,
        container_name="raw-data",
        blob_path="records.jsonl",
        records=[],
        source="test",
        run_id="run",
        dag_id="test",
    )
    manifest["url"] = "https://example.invalid/blob?sig=secret"

    with pytest.raises(ValueError, match="unsupported fields"):
        manifest_module.validate_manifest(manifest)


def test_migrated_dags_do_not_push_record_collections_to_xcom():
    expected_manifest_keys = {
        "census_market_intelligence_refresh.py": (
            "population_manifest",
            "permit_manifest",
        ),
        "eia_refresh.py": ("records_manifest",),
        "fema_refresh.py": ("records_manifest",),
        "nbi_refresh.py": ("records_manifest",),
        "samgov_awards_refresh.py": ("records_manifest",),
        "twdb_water_plan_refresh.py": (
            "twdb_projects_manifest",
            "sdwis_records_manifest",
        ),
    }
    for name, manifest_keys in expected_manifest_keys.items():
        source = (DAGS_DIR / name).read_text()
        assert all(f'key="{key}"' in source for key in manifest_keys)
        assert 'key="eia_records"' not in source
        assert 'key="fema_records"' not in source
        assert 'key="nbi_records"' not in source
        assert 'key="awards"' not in source
        assert 'key="population_data"' not in source
        assert 'key="permit_data"' not in source
        assert 'key="twdb_projects"' not in source
        assert 'key="sdwis_records"' not in source
