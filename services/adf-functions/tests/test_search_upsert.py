import os
import sys
from unittest.mock import MagicMock, patch

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from shared import search_upsert  # noqa: E402


def _prepared(doc_id_prefix="rec1", narrative="A short narrative."):
    return {
        "doc_id_prefix": doc_id_prefix,
        "narrative": narrative,
        "domain": "environmental",
        "document_type": "disaster_declaration",
        "source": "OpenFEMA",
        "source_url": "https://example.com",
    }


@patch("shared.search_upsert._get_search_client")
@patch("shared.search_upsert._get_openai_client")
def test_indexes_all_records_when_embeddings_succeed(mock_get_oai, mock_get_search):
    mock_oai = MagicMock()
    mock_oai.embeddings.create.return_value.data = [MagicMock(embedding=[0.1, 0.2])]
    mock_get_oai.return_value = mock_oai
    mock_search = MagicMock()
    mock_get_search.return_value = mock_search

    count = search_upsert.index_prepared_records([_prepared("a"), _prepared("b")])

    assert count == 2
    mock_search.merge_or_upload_documents.assert_called_once()
    docs = mock_search.merge_or_upload_documents.call_args.kwargs["documents"]
    assert {d["id"] for d in docs} == {"a_0", "b_0"}


@patch("shared.search_upsert._get_search_client")
@patch("shared.search_upsert._get_openai_client")
def test_one_embedding_failure_does_not_abort_the_batch(mock_get_oai, mock_get_search):
    mock_oai = MagicMock()
    mock_oai.embeddings.create.side_effect = [
        Exception("embedding service unavailable"),
        MagicMock(data=[MagicMock(embedding=[0.1])]),
    ]
    mock_get_oai.return_value = mock_oai
    mock_search = MagicMock()
    mock_get_search.return_value = mock_search

    count = search_upsert.index_prepared_records([_prepared("failing"), _prepared("ok")])

    assert count == 1
    docs = mock_search.merge_or_upload_documents.call_args.kwargs["documents"]
    assert docs[0]["id"] == "ok_0"


def test_empty_input_short_circuits_without_clients():
    assert search_upsert.index_prepared_records([]) == 0
