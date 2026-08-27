import logging
import os
import re
import zipfile
from datetime import datetime, timezone
from io import BytesIO
from pathlib import PurePosixPath
from urllib.parse import urlparse

from airflow import DAG
from airflow.providers.standard.operators.python import PythonOperator

log = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
RAW_CONTAINER = "raw-data"
MAX_TWDB_DOWNLOAD_BYTES = 64 * 1024 * 1024
MAX_TWDB_UNCOMPRESSED_BYTES = 256 * 1024 * 1024
MAX_TWDB_ARCHIVE_ENTRIES = 2_048
MAX_TWDB_COMPRESSION_RATIO = 200
TWDB_XLSX_CONTENT_TYPE = (
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
)
TWDB_ZIP_CONTENT_TYPES = {
    "application/zip",
    "application/x-zip-compressed",
    "application/octet-stream",
}

# TWDB planning regions A–P
TWDB_REGIONS = list("ABCDEFGHIJKLMNOP")

# Column name mappings for the TWDB workbook (adapt to actual Excel column headers)
TWDB_COLUMN_MAP = {
    "project_name": ["Project Name", "Strategy Name", "WMS Project Name"],
    "county": ["County", "Counties"],
    "region": [
        "Region",
        "Planning Region",
        "WMS Region",
        "Project Sponsor Region",
        "Planning Region(s) Served by Project",
    ],
    "water_user_group": ["Water User Group", "WUG", "WUG Name"],
    "strategy_type": [
        "Strategy Type",
        "Water Management Strategy",
    ],
    "recommendation_type": ["Project Recommendation Type"],
    "project_components": ["Project Components"],
    "project_sponsor": [
        "Project Sponsor",
        "Sponsor",
        "Entity",
        "List of Project Sponsors",
    ],
    "capital_cost": ["Capital Cost"],
    "cost_2030": ["2030 Capital Cost", "Cost 2030", "Capital Cost 2030"],
    "cost_2040": ["2040 Capital Cost", "Cost 2040", "Capital Cost 2040"],
    "cost_2050": ["2050 Capital Cost", "Cost 2050", "Capital Cost 2050"],
    "cost_2060": ["2060 Capital Cost", "Cost 2060", "Capital Cost 2060"],
    "cost_2070": ["2070 Capital Cost", "Cost 2070", "Capital Cost 2070"],
    "cost_2080": ["2080 Capital Cost", "Cost 2080", "Capital Cost 2080"],
    "volume": ["Water Supply Volume", "Volume (ac-ft/yr)", "Supply Volume"],
    "supply_type": ["Supply Type", "Source", "Water Source Type"],
    "decade_of_need": ["Decade of Need", "Need Decade", "Online Decade"],
}


def _normalize_column_name(value):
    """Normalize agency header whitespace while preserving exact field meaning."""
    return " ".join(str(value).split()).casefold()


def _validate_twdb_url(url):
    """Accept only HTTPS endpoints owned by TWDB, including test subdomains."""
    parsed = urlparse(url)
    hostname = (parsed.hostname or "").casefold()
    if parsed.scheme != "https" or not (
        hostname == "twdb.texas.gov" or hostname.endswith(".twdb.texas.gov")
    ):
        raise ValueError("TWDB workbook URL must be an HTTPS twdb.texas.gov endpoint")


def _validate_archive_entries(archive):
    """Reject traversal, encryption, and zip-bomb-shaped archive entries."""
    entries = archive.infolist()
    if not entries or len(entries) > MAX_TWDB_ARCHIVE_ENTRIES:
        raise ValueError("TWDB workbook archive has an invalid entry count")
    if sum(entry.file_size for entry in entries) > MAX_TWDB_UNCOMPRESSED_BYTES:
        raise ValueError("TWDB workbook archive exceeds the uncompressed size limit")

    for entry in entries:
        path = PurePosixPath(entry.filename.replace("\\", "/"))
        if (
            not entry.filename
            or "\x00" in entry.filename
            or path.is_absolute()
            or ".." in path.parts
        ):
            raise ValueError("TWDB workbook archive contains an unsafe path")
        if entry.flag_bits & 0x1:
            raise ValueError("TWDB workbook archive must not contain encrypted entries")
        if entry.file_size > MAX_TWDB_DOWNLOAD_BYTES:
            raise ValueError("TWDB workbook archive entry exceeds the size limit")
        if entry.file_size and (
            entry.file_size / max(entry.compress_size, 1)
            > MAX_TWDB_COMPRESSION_RATIO
        ):
            raise ValueError("TWDB workbook archive entry has an unsafe compression ratio")


def _is_xlsx_package(archive):
    names = {entry.filename.casefold() for entry in archive.infolist()}
    return "[content_types].xml" in names and "xl/workbook.xml" in names


def _read_bounded_response(response):
    """Read at most the configured source limit without buffering an unbounded body."""
    content_length = response.headers.get("content-length")
    if content_length:
        try:
            declared_length = int(content_length)
        except ValueError as exc:
            raise ValueError(
                "TWDB workbook response has an invalid Content-Length"
            ) from exc
        if declared_length > MAX_TWDB_DOWNLOAD_BYTES:
            raise ValueError("TWDB workbook download exceeds the size limit")

    if hasattr(response, "iter_content"):
        payload = bytearray()
        for chunk in response.iter_content(chunk_size=64 * 1024):
            if not chunk:
                continue
            payload.extend(chunk)
            if len(payload) > MAX_TWDB_DOWNLOAD_BYTES:
                raise ValueError("TWDB workbook download exceeds the size limit")
        return bytes(payload)
    return response.content


def _prepare_twdb_workbook(response, payload):
    """Validate a direct XLSX or agency ZIP and return parse/raw payload metadata."""
    _validate_twdb_url(str(response.url))
    content_type = response.headers.get("content-type", "").split(";", 1)[0].strip().casefold()
    if content_type.startswith("text/") or content_type in {
        "application/xhtml+xml",
        "application/json",
    }:
        raise ValueError(f"TWDB workbook endpoint returned {content_type or 'text content'}")

    if not payload or len(payload) > MAX_TWDB_DOWNLOAD_BYTES:
        raise ValueError("TWDB workbook response is empty or exceeds the size limit")
    if payload.lstrip()[:32].lower().startswith((b"<!doctype html", b"<html")):
        raise ValueError("TWDB workbook endpoint returned HTML instead of a workbook")
    if not zipfile.is_zipfile(BytesIO(payload)):
        raise ValueError("TWDB workbook response is neither an XLSX package nor a ZIP archive")

    with zipfile.ZipFile(BytesIO(payload)) as archive:
        _validate_archive_entries(archive)
        if _is_xlsx_package(archive):
            if content_type and content_type not in {
                TWDB_XLSX_CONTENT_TYPE,
                "application/octet-stream",
            }:
                raise ValueError(f"Unexpected TWDB XLSX content type: {content_type}")
            return payload, payload, ".xlsx", TWDB_XLSX_CONTENT_TYPE

        if content_type and content_type not in TWDB_ZIP_CONTENT_TYPES:
            raise ValueError(f"Unexpected TWDB ZIP content type: {content_type}")
        workbook_entries = [
            entry
            for entry in archive.infolist()
            if not entry.is_dir() and PurePosixPath(entry.filename).suffix.casefold() == ".xlsx"
        ]
        if len(workbook_entries) != 1:
            raise ValueError("TWDB ZIP must contain exactly one XLSX workbook")
        workbook_payload = archive.read(workbook_entries[0])

    if not zipfile.is_zipfile(BytesIO(workbook_payload)):
        raise ValueError("TWDB ZIP contains an invalid XLSX workbook")
    with zipfile.ZipFile(BytesIO(workbook_payload)) as workbook_archive:
        _validate_archive_entries(workbook_archive)
        if not _is_xlsx_package(workbook_archive):
            raise ValueError("TWDB ZIP contains a file that is not an XLSX workbook")

    return workbook_payload, payload, ".zip", "application/zip"

# ---------------------------------------------------------------------------
# DAG definition
# ---------------------------------------------------------------------------
with DAG(
    dag_id="twdb_water_plan_refresh",
    schedule="0 5 1 * *",  # monthly — 1st of month at 05:00 UTC
    start_date=datetime(2024, 1, 1, tzinfo=timezone.utc),
    catchup=False,
    is_paused_upon_creation=True,
    tags=["ingestion", "water", "twdb", "epa"],
    doc_md="""
    ## TWDB Water Plan Refresh DAG

    1. Downloads the TWDB 2027 State Water Plan ZIP workbook from
       `TWDB_WATER_PLAN_WORKBOOK_URL` and indexes all ~3,000 project records
       into Azure AI Search as water plan project narratives.

    2. Pulls EPA SDWIS Texas community water system records from
       `EPA_SDWIS_BASE_URL` and indexes them under domain='water'.

    **Schedule:** Monthly — 1st of month, 05:00 UTC
    **DJM:** Emitted by the Airflow OpenLineage provider through the Datadog transport configured on the scheduler.
    """,
) as dag:

    # -----------------------------------------------------------------------
    # Helper — resolve fuzzy column name
    # -----------------------------------------------------------------------
    def _resolve_col(df_cols, candidates):
        """Return the first matching column name from a list of candidates (case-insensitive)."""
        df_cols_lower = {_normalize_column_name(c): c for c in df_cols}
        for candidate in candidates:
            normalized = _normalize_column_name(candidate)
            if normalized in df_cols_lower:
                return df_cols_lower[normalized]
        return None

    def _read_twdb_sheet(xls, sheet_name):
        """Find the real project header row instead of assuming it is row one."""
        import pandas as pd

        preview = pd.read_excel(
            xls,
            sheet_name=sheet_name,
            header=None,
            nrows=25,
            dtype=str,
        )
        for row_number, row in preview.iterrows():
            values = [_normalize_column_name(value) for value in row if pd.notna(value)]
            if any(
                candidate in values
                for candidate in (
                    _normalize_column_name("Project Name"),
                    _normalize_column_name("Strategy Name"),
                    _normalize_column_name("WMS Project Name"),
                )
            ):
                return pd.read_excel(
                    xls,
                    sheet_name=sheet_name,
                    header=int(row_number),
                    dtype=str,
                )
        return None

    def _project_sheet_names(xls):
        """Prefer the canonical current project sheet over relationship tables."""
        current_sheet = _normalize_column_name("WMSInfrastructureProjects")
        matched = [
            sheet_name
            for sheet_name in xls.sheet_names
            if _normalize_column_name(sheet_name) == current_sheet
        ]
        return matched or list(xls.sheet_names)

    def _project_narrative(project):
        """Render only fields the source actually supplied; never invent blanks."""
        def present(field, default=""):
            value = str(project.get(field, default)).strip()
            return "" if value.casefold() in {"", "nan", "none"} else value

        project_name = present("project_name", "Unnamed project")
        region = present("region")
        heading = "TWDB 2027 Water Plan"
        if region:
            heading += f" — Region {region}"
        parts = [f"{heading}: {project_name}."]

        county = present("county")
        if county:
            parts.append(f"County: {county}.")
        sponsor = present("project_sponsor") or present("water_user_group")
        if sponsor:
            parts.append(f"Project sponsor: {sponsor}.")
        recommendation = present("recommendation_type")
        if recommendation:
            parts.append(f"Recommendation type: {recommendation}.")
        strategy_type = present("strategy_type")
        if strategy_type:
            parts.append(f"Strategy type: {strategy_type}.")
        components = present("project_components")
        if components:
            parts.append(f"Project components: {components}.")

        capital_cost = present("capital_cost")
        decade = present("decade_of_need")
        if not capital_cost:
            for cost_decade in ("2030", "2040", "2050", "2060", "2070", "2080"):
                candidate = present(f"cost_{cost_decade}")
                if candidate and candidate != "0":
                    capital_cost = candidate
                    decade = decade or cost_decade
                    break
        if capital_cost:
            parts.append(f"Estimated capital cost: ${capital_cost}.")
        if decade:
            parts.append(f"Online decade: {decade}.")

        volume = present("volume")
        supply_type = present("supply_type")
        if volume:
            volume_text = f"Published water supply volume: {volume} acre-feet/year"
            if supply_type:
                volume_text += f" ({supply_type})"
            parts.append(f"{volume_text}.")
        elif supply_type:
            parts.append(f"Water supply type: {supply_type}.")
        return " ".join(parts)

    # -----------------------------------------------------------------------
    # Task 1 — fetch_twdb_workbook
    # -----------------------------------------------------------------------
    def fetch_twdb_workbook(**context):
        """Download and safely parse the current TWDB State Water Plan workbook."""
        import pandas as pd
        import requests
        from _blob_manifest import (
            build_run_blob_path,
            get_container_client,
            write_blob_manifest,
            write_records_manifest,
        )

        workbook_url = os.environ["TWDB_WATER_PLAN_WORKBOOK_URL"]
        _validate_twdb_url(workbook_url)

        log.info("Downloading TWDB workbook from: %s", workbook_url)
        resp = requests.get(
            workbook_url,
            timeout=120,
            stream=True,
            allow_redirects=False,
        )
        try:
            if resp.is_redirect:
                raise ValueError(
                    "TWDB workbook endpoint redirects are not permitted; configure the reviewed direct URL"
                )
            resp.raise_for_status()
            response_payload = _read_bounded_response(resp)
            workbook_bytes, raw_bytes, raw_suffix, raw_content_type = (
                _prepare_twdb_workbook(resp, response_payload)
            )
        finally:
            resp.close()

        log.info(
            "Validated TWDB workbook delivery: raw_bytes=%d workbook_bytes=%d format=%s",
            len(raw_bytes),
            len(workbook_bytes),
            raw_suffix,
        )

        # Store raw workbook in Blob Storage
        run_id = str(context.get("run_id") or context.get("ds") or "manual")
        blob_path = build_run_blob_path(
            "twdb", "twdb_water_plan", run_id, raw_suffix
        )
        container = get_container_client(RAW_CONTAINER)
        write_blob_manifest(
            container,
            container_name=RAW_CONTAINER,
            blob_path=blob_path,
            payload=raw_bytes,
            source="twdb.state_water_plan.workbook",
            run_id=run_id,
            record_count=1,
            content_type=raw_content_type,
            content_encoding=None,
            dag_id="twdb_water_plan_refresh",
        )
        log.info("Stored raw TWDB workbook at: %s/%s", RAW_CONTAINER, blob_path)

        # Parse using openpyxl via pandas
        xls = pd.ExcelFile(BytesIO(workbook_bytes), engine="openpyxl")
        all_projects = []

        project_sheets = _project_sheet_names(xls)
        for sheet_name in project_sheets:
            try:
                df = _read_twdb_sheet(xls, sheet_name)
                if df is None:
                    log.debug("Sheet '%s' has no project header row — skipping", sheet_name)
                    continue
                df.columns = [str(c).strip() for c in df.columns]

                # Detect if this sheet has project data by looking for key columns
                name_col = _resolve_col(df.columns, TWDB_COLUMN_MAP["project_name"])
                if name_col is None:
                    log.debug("Sheet '%s' has no project name column — skipping", sheet_name)
                    continue

                log.info("Parsing sheet '%s' with %d rows", sheet_name, len(df))

                for _, row in df.iterrows():
                    project_name = str(row.get(name_col, "")).strip()
                    if not project_name or project_name.lower() in ("nan", "project name", ""):
                        continue

                    def get_val(field):
                        col = _resolve_col(df.columns, TWDB_COLUMN_MAP.get(field, [field]))
                        if col and col in row.index:
                            v = str(row[col]).strip()
                            return v if v.lower() != "nan" else ""
                        return ""

                    project = {
                        "project_name": project_name,
                        "county": get_val("county"),
                        "region": get_val("region"),
                        "water_user_group": get_val("water_user_group"),
                        "strategy_type": get_val("strategy_type"),
                        "recommendation_type": get_val("recommendation_type"),
                        "project_components": get_val("project_components"),
                        "project_sponsor": get_val("project_sponsor"),
                        "capital_cost": get_val("capital_cost"),
                        "cost_2030": get_val("cost_2030"),
                        "cost_2040": get_val("cost_2040"),
                        "cost_2050": get_val("cost_2050"),
                        "cost_2060": get_val("cost_2060"),
                        "cost_2070": get_val("cost_2070"),
                        "cost_2080": get_val("cost_2080"),
                        "volume": get_val("volume"),
                        "supply_type": get_val("supply_type"),
                        "decade_of_need": get_val("decade_of_need"),
                        "sheet": sheet_name,
                    }
                    capital_cost = project["capital_cost"]
                    online_decade = project["decade_of_need"]
                    decade_cost_field = f"cost_{online_decade}"
                    if capital_cost and decade_cost_field in {
                        "cost_2030",
                        "cost_2040",
                        "cost_2050",
                        "cost_2060",
                        "cost_2070",
                        "cost_2080",
                    }:
                        project[decade_cost_field] = capital_cost
                    all_projects.append(project)

            except Exception as exc:
                log.warning("Failed to parse sheet '%s': %s", sheet_name, exc)
                continue

        if not all_projects:
            raise ValueError(
                "TWDB workbook did not contain any recognized project records"
            )
        log.info(
            "Parsed %d TWDB water plan project records from %d selected sheet(s)",
            len(all_projects),
            len(project_sheets),
        )
        projects_manifest = write_records_manifest(
            container,
            container_name=RAW_CONTAINER,
            blob_path=build_run_blob_path(
                "twdb/manifests", "water_plan_projects", run_id, ".jsonl"
            ),
            records=all_projects,
            source="twdb.state_water_plan.projects",
            run_id=run_id,
            dag_id="twdb_water_plan_refresh",
        )
        context["ti"].xcom_push(
            key="twdb_projects_manifest", value=projects_manifest
        )
        return len(all_projects)

    # -----------------------------------------------------------------------
    # Task 2 — fetch_epa_sdwis
    # -----------------------------------------------------------------------
    def fetch_epa_sdwis(**context):
        """Pull EPA SDWIS Texas community water system records from Envirofacts."""
        import pandas as pd
        import requests
        from _blob_manifest import (
            build_run_blob_path,
            get_container_client,
            write_blob_manifest,
            write_records_manifest,
        )

        sdwis_base = os.environ["EPA_SDWIS_BASE_URL"]
        url = f"{sdwis_base}/WATER_SYSTEM/STATE_CODE/TX/PWS_TYPE_CODE/CWS/JSON"

        log.info("Fetching EPA SDWIS Texas CWS records from: %s", url)
        resp = requests.get(url, timeout=120)
        resp.raise_for_status()
        records = resp.json()

        log.info("Fetched %d EPA SDWIS CWS records", len(records))

        # Store raw as parquet
        run_id = str(context.get("run_id") or context.get("ds") or "manual")
        container = get_container_client(RAW_CONTAINER)
        records_manifest = write_records_manifest(
            container,
            container_name=RAW_CONTAINER,
            blob_path=build_run_blob_path(
                "epa_sdwis/manifests", "sdwis_tx_cws", run_id, ".jsonl"
            ),
            records=records,
            source="epa.sdwis.texas.community_water_systems",
            run_id=run_id,
            dag_id="twdb_water_plan_refresh",
        )

        blob_path = build_run_blob_path(
            "epa_sdwis", "sdwis_tx_cws", run_id, ".parquet"
        )
        df = pd.DataFrame(records)
        parquet_buf = BytesIO()
        df.to_parquet(parquet_buf, index=False)
        parquet_buf.seek(0)

        parquet_manifest = write_blob_manifest(
            container,
            container_name=RAW_CONTAINER,
            blob_path=blob_path,
            payload=parquet_buf.getvalue(),
            source="epa.sdwis.texas.community_water_systems.parquet",
            run_id=run_id,
            record_count=len(records),
            content_type="application/vnd.apache.parquet",
            content_encoding=None,
            dag_id="twdb_water_plan_refresh",
        )
        log.info("Stored raw EPA SDWIS Parquet at: %s/%s", RAW_CONTAINER, blob_path)

        context["ti"].xcom_push(key="sdwis_records_manifest", value=records_manifest)
        context["ti"].xcom_push(key="sdwis_parquet_manifest", value=parquet_manifest)
        return len(records)

    # -----------------------------------------------------------------------
    # Task 3 — index_twdb_projects
    # -----------------------------------------------------------------------
    def index_twdb_projects(**context):
        """Convert TWDB project records to text narratives and upsert into Azure AI Search."""
        import tiktoken
        from azure.core.credentials import AzureKeyCredential
        from azure.search.documents import SearchClient
        from openai import AzureOpenAI
        from _blob_manifest import get_container_client, read_records_manifest

        projects_manifest = context["ti"].xcom_pull(
            key="twdb_projects_manifest", task_ids="fetch_twdb_workbook"
        )
        projects = read_records_manifest(
            get_container_client(RAW_CONTAINER),
            projects_manifest,
            expected_source="twdb.state_water_plan.projects",
        )
        if not projects:
            log.warning("No TWDB projects to index — skipping.")
            return

        search_endpoint = os.environ["AZURE_SEARCH_ENDPOINT"]
        search_api_key = os.environ["AZURE_SEARCH_API_KEY"]
        index_name = os.environ["AZURE_SEARCH_INDEX_NAME"]
        openai_endpoint = os.environ["AZURE_OPENAI_ENDPOINT"]
        openai_api_key = os.environ["AZURE_OPENAI_API_KEY"]
        embedding_deployment = os.environ.get(
            "AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "text-embedding-3-small"
        )

        oai_client = AzureOpenAI(
            azure_endpoint=openai_endpoint,
            api_key=openai_api_key,
            api_version="2024-02-01",
        )

        search_client = SearchClient(
            endpoint=search_endpoint,
            index_name=index_name,
            credential=AzureKeyCredential(search_api_key),
        )

        enc = tiktoken.get_encoding("cl100k_base")
        now_iso = datetime.now(timezone.utc).isoformat()
        docs_to_upsert = []

        for idx, proj in enumerate(projects):
            project_name = proj.get("project_name", "Unnamed Project")
            narrative = _project_narrative(proj)

            tokens = enc.encode(narrative)
            chunk_size_tok = 512
            overlap_tok = 64
            token_chunks = []
            start = 0
            while start < len(tokens):
                end = min(start + chunk_size_tok, len(tokens))
                token_chunks.append(enc.decode(tokens[start:end]))
                if end == len(tokens):
                    break
                start += chunk_size_tok - overlap_tok

            for chunk_idx, chunk_text in enumerate(token_chunks):
                safe_name = re.sub(r"[^a-zA-Z0-9_-]", "_", project_name)[:60]
                doc_id = f"twdb_{safe_name}_{idx}_{chunk_idx}"

                embedding_resp = oai_client.embeddings.create(
                    model=embedding_deployment,
                    input=chunk_text,
                )
                vector = embedding_resp.data[0].embedding

                docs_to_upsert.append({
                    "id": doc_id,
                    "content": chunk_text,
                    "content_vector": vector,
                    "source": "TWDB_2027_State_Water_Plan",
                    "document_type": "water_plan_project",
                    "domain": "water",
                    "last_updated": now_iso,
                    "chunk_index": chunk_idx,
                    "source_url": os.environ["TWDB_WATER_PLAN_WORKBOOK_URL"],
                })

                if len(docs_to_upsert) >= 100:
                    search_client.upsert_documents(documents=docs_to_upsert)
                    log.info("Upserted batch of %d TWDB project documents", len(docs_to_upsert))
                    docs_to_upsert = []

        if docs_to_upsert:
            search_client.upsert_documents(documents=docs_to_upsert)
            log.info("Upserted final batch of %d TWDB project documents", len(docs_to_upsert))

        log.info("TWDB water plan project indexing complete for %d records.", len(projects))

    # -----------------------------------------------------------------------
    # Task 4 — index_sdwis_records
    # -----------------------------------------------------------------------
    def index_sdwis_records(**context):
        """Convert EPA SDWIS water system records to text chunks and upsert into Azure AI Search."""
        import tiktoken
        from azure.core.credentials import AzureKeyCredential
        from azure.search.documents import SearchClient
        from openai import AzureOpenAI
        from _blob_manifest import get_container_client, read_records_manifest

        records_manifest = context["ti"].xcom_pull(
            key="sdwis_records_manifest", task_ids="fetch_epa_sdwis"
        )
        records = read_records_manifest(
            get_container_client(RAW_CONTAINER),
            records_manifest,
            expected_source="epa.sdwis.texas.community_water_systems",
        )
        if not records:
            log.warning("No EPA SDWIS records to index — skipping.")
            return

        search_endpoint = os.environ["AZURE_SEARCH_ENDPOINT"]
        search_api_key = os.environ["AZURE_SEARCH_API_KEY"]
        index_name = os.environ["AZURE_SEARCH_INDEX_NAME"]
        openai_endpoint = os.environ["AZURE_OPENAI_ENDPOINT"]
        openai_api_key = os.environ["AZURE_OPENAI_API_KEY"]
        embedding_deployment = os.environ.get(
            "AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "text-embedding-3-small"
        )
        sdwis_base = os.environ["EPA_SDWIS_BASE_URL"]

        oai_client = AzureOpenAI(
            azure_endpoint=openai_endpoint,
            api_key=openai_api_key,
            api_version="2024-02-01",
        )

        search_client = SearchClient(
            endpoint=search_endpoint,
            index_name=index_name,
            credential=AzureKeyCredential(search_api_key),
        )

        enc = tiktoken.get_encoding("cl100k_base")
        now_iso = datetime.now(timezone.utc).isoformat()
        docs_to_upsert = []
        source_url = f"{sdwis_base}/WATER_SYSTEM/STATE_CODE/TX/PWS_TYPE_CODE/CWS/JSON"

        for idx, rec in enumerate(records):
            # Envirofacts returns uppercase field names
            pwsid = rec.get("PWSID", rec.get("pwsid", f"TX{idx:07d}"))
            system_name = rec.get("PWS_NAME", rec.get("pws_name", "Unknown"))
            city = rec.get("CITY_NAME", rec.get("city_name", ""))
            county = rec.get("COUNTY_SERVED", rec.get("county_served", ""))
            population = rec.get("POPULATION_SERVED_COUNT", rec.get("population_served_count", ""))
            primary_source = rec.get("PRIMARY_SOURCE_CODE", rec.get("primary_source_code", ""))
            activity_status = rec.get("PWS_ACTIVITY_CODE", rec.get("pws_activity_code", ""))
            owner_type = rec.get("OWNER_TYPE_CODE", rec.get("owner_type_code", ""))

            narrative = (
                f"EPA SDWIS — Texas Community Water System: {system_name} (PWSID: {pwsid}). "
                f"City: {city}. County: {county}. "
                f"Population served: {population}. Primary water source: {primary_source}. "
                f"System activity status: {activity_status}. Owner type: {owner_type}. "
                f"State: TX. System type: Community Water System (CWS). "
                f"Regulated under the Safe Drinking Water Act (SDWA) and TCEQ."
            )

            tokens = enc.encode(narrative)
            chunk_size_tok = 512
            overlap_tok = 64
            token_chunks = []
            start = 0
            while start < len(tokens):
                end = min(start + chunk_size_tok, len(tokens))
                token_chunks.append(enc.decode(tokens[start:end]))
                if end == len(tokens):
                    break
                start += chunk_size_tok - overlap_tok

            for chunk_idx, chunk_text in enumerate(token_chunks):
                safe_pwsid = re.sub(r"[^a-zA-Z0-9_-]", "_", str(pwsid))
                doc_id = f"sdwis_{safe_pwsid}_{chunk_idx}"

                embedding_resp = oai_client.embeddings.create(
                    model=embedding_deployment,
                    input=chunk_text,
                )
                vector = embedding_resp.data[0].embedding

                docs_to_upsert.append({
                    "id": doc_id,
                    "content": chunk_text,
                    "content_vector": vector,
                    "source": "EPA_SDWIS",
                    "document_type": "water_system_record",
                    "domain": "water",
                    "last_updated": now_iso,
                    "chunk_index": chunk_idx,
                    "source_url": source_url,
                })

                if len(docs_to_upsert) >= 100:
                    search_client.upsert_documents(documents=docs_to_upsert)
                    log.info("Upserted batch of %d SDWIS documents", len(docs_to_upsert))
                    docs_to_upsert = []

        if docs_to_upsert:
            search_client.upsert_documents(documents=docs_to_upsert)
            log.info("Upserted final batch of %d SDWIS documents", len(docs_to_upsert))

        log.info("EPA SDWIS indexing complete for %d water system records.", len(records))

    # -----------------------------------------------------------------------
    # Wire up operators
    # -----------------------------------------------------------------------
    t1_twdb = PythonOperator(
        task_id="fetch_twdb_workbook",
        python_callable=fetch_twdb_workbook,
    )

    t2_sdwis = PythonOperator(
        task_id="fetch_epa_sdwis",
        python_callable=fetch_epa_sdwis,
    )

    t3_twdb_idx = PythonOperator(
        task_id="index_twdb_projects",
        python_callable=index_twdb_projects,
    )

    t4_sdwis_idx = PythonOperator(
        task_id="index_sdwis_records",
        python_callable=index_sdwis_records,
    )

    # Fetch tasks run in parallel; index tasks fan in after
    [t1_twdb, t2_sdwis] >> t3_twdb_idx
    t2_sdwis >> t4_sdwis_idx
