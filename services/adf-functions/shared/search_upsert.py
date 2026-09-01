"""Shared chunk + embed + upsert step, used by every domain.

Each domain's fetch step does its own field mapping and narrative/doc_id
construction (that part is too bespoke to generalize — see each module
under domains/), then writes a list of "prepared records" shaped like:

    {"doc_id_prefix": str, "narrative": str, "domain": str,
     "document_type": str, "source": str, "source_url": str | None}

This module reads that list and does the one standardized part every
domain shares: chunk the narrative, embed each chunk, upsert into the one
shared Azure AI Search index in batches.
"""

import logging
import os
from datetime import datetime, timezone

from azure.core.credentials import AzureKeyCredential
from azure.search.documents import SearchClient
from openai import AzureOpenAI

from .chunking import chunk_text

logger = logging.getLogger(__name__)

_UPSERT_BATCH_SIZE = 100


def _get_search_client() -> SearchClient:
    return SearchClient(
        endpoint=os.environ["AZURE_SEARCH_ENDPOINT"],
        index_name=os.environ["AZURE_SEARCH_INDEX_NAME"],
        credential=AzureKeyCredential(os.environ["AZURE_SEARCH_API_KEY"]),
    )


def _get_openai_client() -> AzureOpenAI:
    return AzureOpenAI(
        azure_endpoint=os.environ["AZURE_OPENAI_ENDPOINT"],
        api_key=os.environ["AZURE_OPENAI_API_KEY"],
        api_version="2024-02-01",
    )


def index_prepared_records(prepared_records: list[dict]) -> int:
    """Chunk, embed, and upsert every prepared record. Returns document count."""
    if not prepared_records:
        return 0

    embedding_deployment = os.environ.get("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "text-embedding-3-small")
    oai_client = _get_openai_client()
    search_client = _get_search_client()
    now_iso = datetime.now(timezone.utc).isoformat()

    docs_to_upsert: list[dict] = []
    total_docs = 0

    for record in prepared_records:
        chunks = chunk_text(record["narrative"])
        for chunk_idx, chunk_text_value in enumerate(chunks):
            try:
                embedding_resp = oai_client.embeddings.create(model=embedding_deployment, input=chunk_text_value)
                vector = embedding_resp.data[0].embedding
            except Exception as exc:
                # One record's embedding failure should never abort the whole
                # batch — log and skip, matching the resilience the original
                # census_market_intelligence_refresh DAG had per-record.
                logger.warning("Embedding failed for doc_id_prefix=%s: %s", record.get("doc_id_prefix"), exc)
                continue
            docs_to_upsert.append({
                "id": f"{record['doc_id_prefix']}_{chunk_idx}",
                "content": chunk_text_value,
                "content_vector": vector,
                "source": record["source"],
                "document_type": record["document_type"],
                "domain": record["domain"],
                "last_updated": now_iso,
                "chunk_index": chunk_idx,
                "source_url": record.get("source_url") or "",
            })
            if len(docs_to_upsert) >= _UPSERT_BATCH_SIZE:
                search_client.merge_or_upload_documents(documents=docs_to_upsert)
                total_docs += len(docs_to_upsert)
                logger.info("Upserted batch of %d documents", len(docs_to_upsert))
                docs_to_upsert = []

    if docs_to_upsert:
        search_client.merge_or_upload_documents(documents=docs_to_upsert)
        total_docs += len(docs_to_upsert)
        logger.info("Upserted final batch of %d documents", len(docs_to_upsert))

    return total_docs


def count_indexed_documents(filter_expr: str) -> int:
    """Used by public_docs_ingestion's idempotency gate (ADF Web Activity
    calls Azure AI Search's REST $count endpoint directly instead — this
    helper exists for local testing / any function-based caller)."""
    search_client = _get_search_client()
    results = search_client.search(search_text="*", filter=filter_expr, include_total_count=True, top=0)
    return results.get_count() or 0
