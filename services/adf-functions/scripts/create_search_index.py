"""One-off / idempotent setup script: create the infra-advisor-knowledge
Azure AI Search index if it doesn't already exist.

Schema is taken verbatim from specs/infraadvisor-prd.md's "Azure AI Search
index schema" section — this script exists because that schema was never
actually provisioned in this environment (discovered while validating the
ADF migration: every indexing call failed with ResourceNotFoundError since
the index itself didn't exist, unrelated to any Airflow/ADF pipeline bug).

Usage:
    uv run python scripts/create_search_index.py
"""

import os

from azure.core.credentials import AzureKeyCredential
from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    HnswAlgorithmConfiguration,
    SearchableField,
    SearchField,
    SearchFieldDataType,
    SearchIndex,
    SimpleField,
    VectorSearch,
    VectorSearchProfile,
)

INDEX_NAME = "infra-advisor-knowledge"
VECTOR_DIMENSIONS = 1536  # text-embedding-3-small


def build_index() -> SearchIndex:
    fields = [
        SimpleField(name="id", type=SearchFieldDataType.String, key=True),
        SearchableField(name="content", type=SearchFieldDataType.String),
        SearchField(
            name="content_vector",
            type=SearchFieldDataType.Collection(SearchFieldDataType.Single),
            searchable=True,
            vector_search_dimensions=VECTOR_DIMENSIONS,
            vector_search_profile_name="hnsw-profile",
        ),
        SimpleField(name="source", type=SearchFieldDataType.String, filterable=True),
        SimpleField(name="document_type", type=SearchFieldDataType.String, filterable=True),
        SimpleField(name="domain", type=SearchFieldDataType.String, filterable=True),
        SimpleField(
            name="last_updated",
            type=SearchFieldDataType.DateTimeOffset,
            filterable=True,
            sortable=True,
        ),
        SimpleField(name="chunk_index", type=SearchFieldDataType.Int32),
        SimpleField(name="source_url", type=SearchFieldDataType.String),
    ]

    vector_search = VectorSearch(
        profiles=[VectorSearchProfile(name="hnsw-profile", algorithm_configuration_name="hnsw-config")],
        algorithms=[HnswAlgorithmConfiguration(name="hnsw-config")],
    )

    return SearchIndex(name=INDEX_NAME, fields=fields, vector_search=vector_search)


def main() -> None:
    endpoint = os.environ["AZURE_SEARCH_ENDPOINT"]
    api_key = os.environ["AZURE_SEARCH_API_KEY"]
    client = SearchIndexClient(endpoint=endpoint, credential=AzureKeyCredential(api_key))

    existing = [idx.name for idx in client.list_indexes()]
    if INDEX_NAME in existing:
        print(f"Index '{INDEX_NAME}' already exists — nothing to do.")
        return

    client.create_index(build_index())
    print(f"Created index '{INDEX_NAME}'.")


if __name__ == "__main__":
    main()
