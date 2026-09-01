"""The one standardized chunker every domain uses via search_upsert.py.

Replaces three inconsistent implementations that existed across the old
Airflow DAGs: fema/eia/census/twdb each reimplemented this inline (512
tokens/64 overlap via tiktoken), nbi_refresh.py used raw 500-character
splitting (no tiktoken), and samgov_awards_refresh.py used 512-token
chunks with no overlap. Standardizing here means every domain's chunk
boundaries are now consistent and this logic lives in exactly one place.
"""

import tiktoken

_ENCODING = tiktoken.get_encoding("cl100k_base")


def chunk_text(text: str, max_tokens: int = 512, overlap_tokens: int = 64) -> list[str]:
    tokens = _ENCODING.encode(text)
    if not tokens:
        return []
    chunks: list[str] = []
    start = 0
    while start < len(tokens):
        end = min(start + max_tokens, len(tokens))
        chunks.append(_ENCODING.decode(tokens[start:end]))
        if end == len(tokens):
            break
        start = end - overlap_tokens
    return chunks
