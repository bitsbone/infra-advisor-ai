"""Azure Functions app hosting every ADF-invoked step for the migrated
ingestion pipelines. One Function App, many named HTTP-triggered
functions — mirrors the old single-image-many-DAGs Airflow deployment
model and keeps CI/CD to one publish step.

Each function is called by an ADF Function Activity with a JSON request
body and returns a JSON response consumed by the next activity in the
pipeline (see infra/bicep/modules/data-factory.bicep for the pipeline
definitions).
"""

# Consumption-plan Python Azure Functions have no agent sidecar to send
# traces to — Datadog's serverless compat layer submits directly to
# Datadog's intake instead. Must start before ddtrace.auto, which itself
# must be the first import that touches any instrumented library (requests,
# httpx, azure-storage-blob, azure-search-documents, openai). DD_AGENT_HOST
# is deliberately never set for this service — see DD_SITE/DD_API_KEY in
# infra/bicep/modules/adf-functions.bicep instead.
from datadog_serverless_compat import start

start()

import ddtrace.auto  # noqa: E402, F401

import json
import logging

import azure.functions as func
from ddtrace.llmobs import LLMObs

from shared.blob_io import PREPARED_CONTAINER, read_json_records
from shared.search_upsert import index_prepared_records

logger = logging.getLogger(__name__)

try:
    LLMObs.enable(ml_app="infra-advisor-ai", agentless_enabled=True)
except Exception:
    logger.warning("LLMObs.enable() failed (non-fatal)", exc_info=True)

app = func.FunctionApp(http_auth_level=func.AuthLevel.FUNCTION)


def _json_response(payload: dict) -> func.HttpResponse:
    return func.HttpResponse(json.dumps(payload), mimetype="application/json")


def _request_body(req: func.HttpRequest) -> dict:
    try:
        return req.get_json()
    except ValueError:
        return {}


# ── fema (daily) ──────────────────────────────────────────────────────────────

@app.route(route="fetch-and-store-fema", methods=["POST"])
def fetch_and_store_fema(req: func.HttpRequest) -> func.HttpResponse:
    from domains import fema
    body = _request_body(req)
    return _json_response(fema.fetch_and_store(run_id=body["run_id"]))


# ── nbi (weekly) ──────────────────────────────────────────────────────────────

@app.route(route="fetch-and-store-nbi", methods=["POST"])
def fetch_and_store_nbi(req: func.HttpRequest) -> func.HttpResponse:
    from domains import nbi
    body = _request_body(req)
    return _json_response(nbi.fetch_and_store(run_id=body["run_id"]))


# ── eia (weekly) ──────────────────────────────────────────────────────────────

@app.route(route="fetch-and-store-eia", methods=["POST"])
def fetch_and_store_eia(req: func.HttpRequest) -> func.HttpResponse:
    from domains import eia
    body = _request_body(req)
    return _json_response(eia.fetch_and_store(run_id=body["run_id"]))


# ── samgov / usaspending (weekly) ─────────────────────────────────────────────

@app.route(route="fetch-and-store-samgov", methods=["POST"])
def fetch_and_store_samgov(req: func.HttpRequest) -> func.HttpResponse:
    from domains import samgov
    body = _request_body(req)
    return _json_response(samgov.fetch_and_store(run_id=body["run_id"]))


# ── census (monthly, fan-in) ──────────────────────────────────────────────────

@app.route(route="fetch-census-population", methods=["POST"])
def fetch_census_population(req: func.HttpRequest) -> func.HttpResponse:
    from domains import census
    body = _request_body(req)
    return _json_response(census.fetch_population(run_id=body["run_id"]))


@app.route(route="fetch-census-permits", methods=["POST"])
def fetch_census_permits(req: func.HttpRequest) -> func.HttpResponse:
    from domains import census
    body = _request_body(req)
    return _json_response(census.fetch_permits(run_id=body["run_id"]))


@app.route(route="build-census-prepared-records", methods=["POST"])
def build_census_prepared_records(req: func.HttpRequest) -> func.HttpResponse:
    from domains import census
    body = _request_body(req)
    return _json_response(census.build_prepared_records(
        run_id=body["run_id"],
        population_blob_path=body["population_blob_path"],
        permits_blob_path=body["permits_blob_path"],
    ))


# ── public docs (weekly, idempotency-gated) ───────────────────────────────────

@app.route(route="public-docs-report-builder", methods=["POST"])
def public_docs_report_builder(req: func.HttpRequest) -> func.HttpResponse:
    from domains import public_docs
    body = _request_body(req)
    return _json_response(public_docs.fetch_and_prepare(run_id=body["run_id"]))


# ── shared indexing step, used by every domain above ──────────────────────────

@app.route(route="index-search-shared", methods=["POST"])
def index_search_shared(req: func.HttpRequest) -> func.HttpResponse:
    body = _request_body(req)
    prepared_blob_path = body.get("prepared_blob_path")
    if not prepared_blob_path:
        logger.info("No prepared_blob_path provided (empty/skipped upstream fetch) — nothing to index.")
        return _json_response({"indexed_document_count": 0})

    prepared_records = read_json_records(PREPARED_CONTAINER, prepared_blob_path)
    indexed_count = index_prepared_records(prepared_records)
    return _json_response({"indexed_document_count": indexed_count})
