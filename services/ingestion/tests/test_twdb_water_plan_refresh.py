"""
Tests for the TWDB water plan + EPA SDWIS DAG.

Two independent fetch tasks are tested:

* fetch_twdb_workbook  — downloads an Excel workbook (mocked as bytes) and
                         parses TWDB water plan project records.
* fetch_epa_sdwis      — calls EPA Envirofacts at the path
                         WATER_SYSTEM/STATE_CODE/TX/PWS_TYPE_CODE/CWS/JSON
                         and returns water system records keyed by PWSID.

All HTTP traffic is intercepted with ``respx``.
Azure SDK calls are patched with ``unittest.mock``.
"""

import importlib.util as ilu
import io
import os
import sys
import types
import zipfile
from unittest.mock import MagicMock, patch

import pandas as pd
import pytest
import respx
import httpx
from httpx import Response

# ---------------------------------------------------------------------------
# Env-var fixture
# ---------------------------------------------------------------------------

@pytest.fixture(autouse=True)
def mock_env(monkeypatch):
    monkeypatch.syspath_prepend(
        os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "dags"))
    )
    monkeypatch.setenv("AZURE_SEARCH_ENDPOINT", "https://mock.search.windows.net")
    monkeypatch.setenv("AZURE_SEARCH_API_KEY", "mock-key")
    monkeypatch.setenv("AZURE_SEARCH_INDEX_NAME", "infra-advisor-knowledge")
    monkeypatch.setenv("AZURE_OPENAI_ENDPOINT", "https://mock.openai.azure.com")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "mock-key")
    monkeypatch.setenv(
        "AZURE_STORAGE_CONNECTION_STRING",
        "DefaultEndpointsProtocol=https;AccountName=mock;AccountKey=bW9jaw==;EndpointSuffix=core.windows.net",
    )
    monkeypatch.setenv("EIA_API_KEY", "mock-key")
    monkeypatch.setenv("DD_AGENT_HOST", "localhost")
    monkeypatch.setenv(
        "TWDB_WATER_PLAN_WORKBOOK_URL",
        "https://mock.twdb.texas.gov/waterplan2026.xlsx",
    )
    monkeypatch.setenv(
        "EPA_SDWIS_BASE_URL",
        "https://enviro.epa.gov/enviro/efservice",
    )
    # Production uses streaming requests, while respx intercepts httpx. Route
    # the same GET contract through httpx so no test can reach the network.
    def intercepted_get(url, **kwargs):
        kwargs.pop("stream", None)
        allow_redirects = kwargs.pop("allow_redirects", False)
        return httpx.get(url, follow_redirects=allow_redirects, **kwargs)

    monkeypatch.setattr("requests.get", intercepted_get)


# ---------------------------------------------------------------------------
# Stubs for heavy optional dependencies
# ---------------------------------------------------------------------------

def _stub_module(name: str, **attrs):
    if name in sys.modules:
        return
    mod = types.ModuleType(name)
    for k, v in attrs.items():
        setattr(mod, k, v)
    sys.modules[name] = mod


class _OperatorStub(dict):
    """Keep operator kwargs inspectable while supporting DAG dependency syntax."""

    def __rshift__(self, other):
        return other

    def __rrshift__(self, other):
        return self


def _ensure_stubs():
    _stub_module("ddtrace")
    _stub_module("ddtrace.auto")

    if "airflow" not in sys.modules:
        dag_cls = MagicMock()
        dag_instance = MagicMock()
        dag_cls.return_value.__enter__ = lambda s, *a: dag_instance
        dag_cls.return_value.__exit__ = MagicMock(return_value=False)
        _stub_module("airflow", DAG=dag_cls)
        _stub_module("airflow.providers", standard=MagicMock())
        _stub_module("airflow.providers.standard", operators=MagicMock())
        _stub_module("airflow.providers.standard.operators", python=MagicMock())
        _stub_module(
            "airflow.providers.standard.operators.python",
            PythonOperator=MagicMock(side_effect=lambda **kw: _OperatorStub(kw)),
        )


_ensure_stubs()

# ---------------------------------------------------------------------------
# Load TWDB DAG module
# ---------------------------------------------------------------------------

DAG_PATH = os.path.abspath(
    os.path.join(os.path.dirname(__file__), "..", "dags", "twdb_water_plan_refresh.py")
)

_twdb_spec = ilu.spec_from_file_location("twdb_water_plan_refresh", DAG_PATH)
_twdb_mod = ilu.module_from_spec(_twdb_spec)


@pytest.fixture(scope="module")
def twdb_module():
    with patch.dict(sys.modules, {"ddtrace.auto": MagicMock()}), patch(
        "requests.get", MagicMock()
    ):
        _twdb_spec.loader.exec_module(_twdb_mod)
    return _twdb_mod


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

TWDB_WORKBOOK_URL = "https://mock.twdb.texas.gov/waterplan2026.xlsx"
EPA_SDWIS_BASE = "https://enviro.epa.gov/enviro/efservice"
EPA_SDWIS_URL = f"{EPA_SDWIS_BASE}/WATER_SYSTEM/STATE_CODE/TX/PWS_TYPE_CODE/CWS/JSON"


class FakeTI:
    def __init__(self):
        self._store = {}

    def xcom_push(self, key, value):
        self._store[key] = value

    def xcom_pull(self, key, task_ids=None):
        return self._store.get(key)


def _make_workbook_bytes(rows: list[dict]) -> bytes:
    """Return a minimal Excel workbook as bytes using pandas + openpyxl."""
    if not rows:
        rows = [{"Project Name": "", "Region": "", "County": ""}]
    df = pd.DataFrame(rows)
    buf = io.BytesIO()
    df.to_excel(buf, index=False, engine="openpyxl")
    buf.seek(0)
    return buf.read()


def _make_workbook_zip(workbook_bytes: bytes, path: str = "release/data/workbook.xlsx") -> bytes:
    """Return the nested ZIP delivery shape used by the current TWDB endpoint."""
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(path, workbook_bytes)
    return buf.getvalue()


def _make_current_twdb_workbook_bytes(rows: list[dict]) -> bytes:
    """Return a workbook with the title rows and headers published by TWDB in 2026."""
    headers = [
        "WMS \nProject\nId",
        "Project \nSponsor \nRegion",
        "Project \nRecommendation \nType",
        "Project Name",
        "Online \nDecade",
        "Planning Region(s) \nServed by Project",
        "List of Project Sponsors",
        "Capital Cost",
        "Project Components",
    ]
    title_rows = [
        ["Data Tab 8 - WMS Infrastructure Projects"],
        ["Click here to go to the worksheet descriptions tab."],
        ["Click here to go to the worksheet column descriptions tab."],
    ]
    data_rows = [
        [
            str(index + 1),
            row.get("region", "A"),
            "Recommended",
            row["project_name"],
            row.get("online_decade", "2030"),
            row.get("region", "A"),
            row.get("sponsor", "Test Water Authority"),
            row.get("capital_cost", "2500000"),
            row.get("components", "Transmission pipeline"),
        ]
        for index, row in enumerate(rows)
    ]
    frame = pd.DataFrame(title_rows + [headers] + data_rows)
    buf = io.BytesIO()
    with pd.ExcelWriter(buf, engine="openpyxl") as writer:
        frame.to_excel(writer, sheet_name="WMSInfrastructureProjects", index=False, header=False)
        relationship_rows = title_rows + [headers] + data_rows + data_rows
        pd.DataFrame(relationship_rows).to_excel(
            writer,
            sheet_name="WMSSupply&ProjectRelationships",
            index=False,
            header=False,
        )
    return buf.getvalue()


def _make_project_rows(n: int = 3) -> list[dict]:
    return [
        {
            "Project Name": f"Aquifer Storage Project {i}",
            "Region": chr(ord("A") + (i % 16)),
            "County": "Travis",
            "Water User Group": "City of Austin",
            "Strategy Type": "Aquifer Storage and Recovery",
            "Project Sponsor": "LCRA",
            "2030 Capital Cost": f"{10 + i}",
            "2040 Capital Cost": "",
            "2050 Capital Cost": "",
            "2060 Capital Cost": "",
            "2070 Capital Cost": "",
            "2080 Capital Cost": "",
            "Water Supply Volume": f"{500 * (i + 1)}",
            "Supply Type": "Groundwater",
            "Decade of Need": "2030",
        }
        for i in range(n)
    ]


def _make_sdwis_records(n: int = 2) -> list[dict]:
    return [
        {
            "PWSID": f"TX{1000000 + i:07d}",
            "PWS_NAME": f"City of Test {i} Water System",
            "CITY_NAME": f"Testville{i}",
            "COUNTY_SERVED": "Harris",
            "POPULATION_SERVED_COUNT": str(50000 + i * 1000),
            "PRIMARY_SOURCE_CODE": "GW",
            "PWS_ACTIVITY_CODE": "A",
            "OWNER_TYPE_CODE": "L",
        }
        for i in range(n)
    ]


def _capture_records_manifest(captured):
    def capture(*args, records, **kwargs):
        captured.extend(records)
        return {
            "schema_version": "1.0",
            "source": kwargs["source"],
            "run_id": kwargs["run_id"],
            "blob": {
                "container": kwargs["container_name"],
                "path": kwargs["blob_path"],
            },
            "record_count": len(records),
            "checksum": {"algorithm": "sha256", "value": "0" * 64},
            "content_type": "application/x-ndjson",
            "content_encoding": "utf-8",
        }

    return capture


# ---------------------------------------------------------------------------
# DAG-level smoke tests
# ---------------------------------------------------------------------------

class TestTwdbDagLoads:
    def test_raw_container_constant(self, twdb_module):
        assert twdb_module.RAW_CONTAINER == "raw-data"

    def test_twdb_regions_has_16_entries(self, twdb_module):
        # A through P inclusive = 16 regions
        assert len(twdb_module.TWDB_REGIONS) == 16
        assert "A" in twdb_module.TWDB_REGIONS
        assert "P" in twdb_module.TWDB_REGIONS

    def test_column_map_has_required_keys(self, twdb_module):
        required_keys = [
            "project_name", "county", "region", "cost_2030", "cost_2080", "volume",
        ]
        for key in required_keys:
            assert key in twdb_module.TWDB_COLUMN_MAP, f"Missing column map key: {key}"


# ---------------------------------------------------------------------------
# fetch_twdb_workbook
# ---------------------------------------------------------------------------

class TestFetchTwdbWorkbook:
    @respx.mock
    def test_extracts_current_twdb_zip_and_detects_real_header_row(self, twdb_module, mock_env):
        workbook_bytes = _make_current_twdb_workbook_bytes(
            [
                {
                    "project_name": "Regional Resilience Pipeline",
                    "region": "N",
                    "online_decade": "2040",
                    "capital_cost": "125000000",
                }
            ]
        )
        response_bytes = _make_workbook_zip(workbook_bytes)
        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(
                200,
                content=response_bytes,
                headers={"content-type": "application/x-zip-compressed"},
            )
        )

        captured = []
        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob, patch(
            "_blob_manifest.write_records_manifest",
            side_effect=_capture_records_manifest(captured),
        ):
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()
            result = twdb_module.fetch_twdb_workbook(ti=FakeTI(), ds="2026-04-01")

        assert result == 1
        assert captured[0]["project_name"] == "Regional Resilience Pipeline"
        assert captured[0]["region"] == "N"
        assert captured[0]["project_sponsor"] == "Test Water Authority"
        assert captured[0]["recommendation_type"] == "Recommended"
        assert captured[0]["project_components"] == "Transmission pipeline"
        assert captured[0]["decade_of_need"] == "2040"
        assert captured[0]["capital_cost"] == "125000000"
        assert captured[0]["cost_2040"] == "125000000"

    @respx.mock
    def test_rejects_html_landing_page_before_blob_write(self, twdb_module, mock_env):
        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(
                200,
                text="<!doctype html><html><body>download page</body></html>",
                headers={"content-type": "text/html"},
            )
        )

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            with pytest.raises(ValueError, match="returned text/html"):
                twdb_module.fetch_twdb_workbook(ti=FakeTI(), ds="2026-04-01")
        mock_blob.assert_not_called()

    @respx.mock
    def test_rejects_redirect_without_fetching_the_destination(self, twdb_module, mock_env):
        redirect_route = respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(
                302,
                headers={"location": "https://untrusted.example/workbook.zip"},
            )
        )
        destination_route = respx.get(
            "https://untrusted.example/workbook.zip"
        ).mock(return_value=Response(200, content=b"untrusted"))

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            with pytest.raises(ValueError, match="redirects are not permitted"):
                twdb_module.fetch_twdb_workbook(ti=FakeTI(), ds="2026-04-01")
        assert redirect_route.called
        assert not destination_route.called
        mock_blob.assert_not_called()

    @respx.mock
    def test_rejects_unsafe_workbook_archive_path(self, twdb_module, mock_env):
        workbook_bytes = _make_workbook_bytes(_make_project_rows(1))
        response_bytes = _make_workbook_zip(workbook_bytes, "../workbook.xlsx")
        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(
                200,
                content=response_bytes,
                headers={"content-type": "application/zip"},
            )
        )

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            with pytest.raises(ValueError, match="unsafe path"):
                twdb_module.fetch_twdb_workbook(ti=FakeTI(), ds="2026-04-01")
        mock_blob.assert_not_called()

    @respx.mock
    def test_rejects_valid_workbook_without_project_records(self, twdb_module, mock_env):
        workbook_bytes = _make_workbook_bytes([])
        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(200, content=workbook_bytes)
        )

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()
            with pytest.raises(ValueError, match="recognized project records"):
                twdb_module.fetch_twdb_workbook(ti=FakeTI(), ds="2026-04-01")

    @respx.mock
    def test_downloads_workbook_from_env_url(self, twdb_module, mock_env):
        rows = _make_project_rows(2)
        workbook_bytes = _make_workbook_bytes(rows)

        route = respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(200, content=workbook_bytes)
        )

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_twdb_workbook(ti=ti, ds="2026-04-01")

        assert route.called

    @respx.mock
    def test_returns_project_count(self, twdb_module, mock_env):
        rows = _make_project_rows(5)
        workbook_bytes = _make_workbook_bytes(rows)

        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(200, content=workbook_bytes)
        )

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            result = twdb_module.fetch_twdb_workbook(ti=ti, ds="2026-04-01")

        assert result == 5

    @respx.mock
    def test_projects_manifest_pushed_to_xcom(self, twdb_module, mock_env):
        rows = _make_project_rows(3)
        workbook_bytes = _make_workbook_bytes(rows)

        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(200, content=workbook_bytes)
        )

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_twdb_workbook(ti=ti, ds="2026-04-01")

        manifest = ti.xcom_pull("twdb_projects_manifest")
        assert manifest["schema_version"] == "1.0"
        assert manifest["record_count"] == 3
        assert ti.xcom_pull("twdb_projects") is None

    @respx.mock
    def test_project_has_region_code(self, twdb_module, mock_env):
        rows = _make_project_rows(1)
        rows[0]["Region"] = "C"
        workbook_bytes = _make_workbook_bytes(rows)

        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(200, content=workbook_bytes)
        )

        captured = []
        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob, patch(
            "_blob_manifest.write_records_manifest",
            side_effect=_capture_records_manifest(captured),
        ):
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_twdb_workbook(ti=ti, ds="2026-04-01")

        assert captured[0]["region"] == "C"

    @respx.mock
    def test_project_has_cost_fields(self, twdb_module, mock_env):
        rows = _make_project_rows(1)
        rows[0]["2030 Capital Cost"] = "42"
        workbook_bytes = _make_workbook_bytes(rows)

        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(200, content=workbook_bytes)
        )

        captured = []
        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob, patch(
            "_blob_manifest.write_records_manifest",
            side_effect=_capture_records_manifest(captured),
        ):
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_twdb_workbook(ti=ti, ds="2026-04-01")

        assert "cost_2030" in captured[0]
        assert captured[0]["cost_2030"] == "42"

    @respx.mock
    def test_project_record_keys(self, twdb_module, mock_env):
        """Parsed project dicts must include all canonical field names."""
        rows = _make_project_rows(1)
        workbook_bytes = _make_workbook_bytes(rows)

        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(200, content=workbook_bytes)
        )

        captured = []
        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob, patch(
            "_blob_manifest.write_records_manifest",
            side_effect=_capture_records_manifest(captured),
        ):
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_twdb_workbook(ti=ti, ds="2026-04-01")

        project = captured[0]
        for key in [
            "project_name", "county", "region", "water_user_group",
            "strategy_type", "project_sponsor", "cost_2030", "cost_2040",
            "cost_2050", "cost_2060", "cost_2070", "cost_2080",
            "volume", "supply_type", "decade_of_need",
        ]:
            assert key in project, f"Expected key '{key}' missing from project dict"

    @respx.mock
    def test_raises_on_500(self, twdb_module, mock_env):
        respx.get(TWDB_WORKBOOK_URL).mock(return_value=Response(500, text="Server Error"))

        with pytest.raises(Exception):
            twdb_module.fetch_twdb_workbook(ti=FakeTI(), ds="2026-04-01")


# ---------------------------------------------------------------------------
# fetch_epa_sdwis
# ---------------------------------------------------------------------------

class TestFetchEpaSdwis:
    @respx.mock
    def test_calls_correct_sdwis_path(self, twdb_module, mock_env):
        """Must request STATE_CODE/TX/PWS_TYPE_CODE/CWS."""
        records = _make_sdwis_records(2)
        route = respx.get(EPA_SDWIS_URL).mock(
            return_value=Response(200, json=records)
        )

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_epa_sdwis(ti=ti, ds="2026-04-01")

        assert route.called
        called_url = str(route.calls[0].request.url)
        assert "STATE_CODE" in called_url
        assert "TX" in called_url
        assert "PWS_TYPE_CODE" in called_url
        assert "CWS" in called_url

    @respx.mock
    def test_returns_record_count(self, twdb_module, mock_env):
        records = _make_sdwis_records(4)
        respx.get(EPA_SDWIS_URL).mock(return_value=Response(200, json=records))

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            result = twdb_module.fetch_epa_sdwis(ti=ti, ds="2026-04-01")

        assert result == 4

    @respx.mock
    def test_records_manifest_pushed_to_xcom(self, twdb_module, mock_env):
        records = _make_sdwis_records(3)
        respx.get(EPA_SDWIS_URL).mock(return_value=Response(200, json=records))

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_epa_sdwis(ti=ti, ds="2026-04-01")

        manifest = ti.xcom_pull("sdwis_records_manifest")
        assert manifest["schema_version"] == "1.0"
        assert manifest["record_count"] == 3
        assert ti.xcom_pull("sdwis_records") is None

    @respx.mock
    def test_records_contain_pwsid_field(self, twdb_module, mock_env):
        records = _make_sdwis_records(1)
        respx.get(EPA_SDWIS_URL).mock(return_value=Response(200, json=records))

        captured = []
        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob, patch(
            "_blob_manifest.write_records_manifest",
            side_effect=_capture_records_manifest(captured),
        ):
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_epa_sdwis(ti=ti, ds="2026-04-01")

        record = captured[0]
        assert "PWSID" in record
        assert record["PWSID"].startswith("TX")

    @respx.mock
    def test_record_has_system_name_and_city(self, twdb_module, mock_env):
        records = _make_sdwis_records(1)
        respx.get(EPA_SDWIS_URL).mock(return_value=Response(200, json=records))

        captured = []
        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob, patch(
            "_blob_manifest.write_records_manifest",
            side_effect=_capture_records_manifest(captured),
        ):
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_epa_sdwis(ti=ti, ds="2026-04-01")

        record = captured[0]
        assert "PWS_NAME" in record
        assert "CITY_NAME" in record
        assert "POPULATION_SERVED_COUNT" in record

    @respx.mock
    def test_raises_on_500(self, twdb_module, mock_env):
        respx.get(EPA_SDWIS_URL).mock(return_value=Response(500, text="Server Error"))

        with pytest.raises(Exception):
            twdb_module.fetch_epa_sdwis(ti=FakeTI(), ds="2026-04-01")

    @respx.mock
    def test_empty_response_returns_zero(self, twdb_module, mock_env):
        respx.get(EPA_SDWIS_URL).mock(return_value=Response(200, json=[]))

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            result = twdb_module.fetch_epa_sdwis(ti=ti, ds="2026-04-01")

        assert result == 0
        assert ti.xcom_pull("sdwis_records_manifest")["record_count"] == 0


# ---------------------------------------------------------------------------
# Both endpoints mocked simultaneously
# ---------------------------------------------------------------------------

class TestBothEndpointsTogether:
    @respx.mock
    def test_independent_fetch_tasks_do_not_interfere(self, twdb_module, mock_env):
        """Fetch tasks should each push independent xcom keys."""
        workbook_rows = _make_project_rows(2)
        workbook_bytes = _make_workbook_bytes(workbook_rows)
        sdwis_records = _make_sdwis_records(2)

        respx.get(TWDB_WORKBOOK_URL).mock(
            return_value=Response(200, content=workbook_bytes)
        )
        respx.get(EPA_SDWIS_URL).mock(
            return_value=Response(200, json=sdwis_records)
        )

        with patch("azure.storage.blob.BlobServiceClient.from_connection_string") as mock_blob:
            mock_blob.return_value.get_container_client.return_value.create_container = MagicMock()
            mock_blob.return_value.get_container_client.return_value.get_blob_client.return_value.upload_blob = MagicMock()

            ti = FakeTI()
            twdb_module.fetch_twdb_workbook(ti=ti, ds="2026-04-01")
            twdb_module.fetch_epa_sdwis(ti=ti, ds="2026-04-01")

        projects = ti.xcom_pull("twdb_projects_manifest")
        sdwis = ti.xcom_pull("sdwis_records_manifest")

        assert projects["record_count"] == 2
        assert sdwis["record_count"] == 2
        assert projects["source"] == "twdb.state_water_plan.projects"
        assert sdwis["source"] == "epa.sdwis.texas.community_water_systems"


# ---------------------------------------------------------------------------
# Resolve-column helper
# ---------------------------------------------------------------------------

class TestResolveCol:
    def test_resolves_exact_match(self, twdb_module):
        cols = ["Project Name", "County", "Region"]
        result = twdb_module._resolve_col(cols, ["Project Name"])
        assert result == "Project Name"

    def test_resolves_case_insensitive(self, twdb_module):
        cols = ["project name", "County"]
        result = twdb_module._resolve_col(cols, ["Project Name"])
        assert result == "project name"

    def test_returns_first_candidate_match(self, twdb_module):
        cols = ["WMS Project Name", "Strategy Name"]
        result = twdb_module._resolve_col(cols, ["Project Name", "Strategy Name", "WMS Project Name"])
        assert result == "Strategy Name"

    def test_returns_none_when_no_match(self, twdb_module):
        cols = ["Foo", "Bar"]
        result = twdb_module._resolve_col(cols, ["Project Name", "WMS Project Name"])
        assert result is None


class TestProjectNarrative:
    def test_omits_fields_that_are_absent_from_current_workbook(self, twdb_module):
        narrative = twdb_module._project_narrative(
            {
                "project_name": "Regional Resilience Pipeline",
                "region": "N",
                "project_sponsor": "Test Water Authority",
                "recommendation_type": "Recommended",
                "project_components": "Transmission pipeline",
                "capital_cost": "125000000",
                "decade_of_need": "2040",
                "county": "",
                "volume": "",
                "supply_type": "",
            }
        )

        assert "TWDB 2027 Water Plan — Region N" in narrative
        assert "Project sponsor: Test Water Authority" in narrative
        assert "Recommendation type: Recommended" in narrative
        assert "Project components: Transmission pipeline" in narrative
        assert "Estimated capital cost: $125000000" in narrative
        assert "Online decade: 2040" in narrative
        assert "County:" not in narrative
        assert "acre-feet/year" not in narrative
