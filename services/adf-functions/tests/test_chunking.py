import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from shared.chunking import chunk_text  # noqa: E402


def test_short_text_produces_one_chunk():
    chunks = chunk_text("A short sentence.")
    assert len(chunks) == 1
    assert chunks[0] == "A short sentence."


def test_empty_text_produces_no_chunks():
    assert chunk_text("") == []


def test_long_text_produces_overlapping_chunks():
    # ~2000 tokens of repeated words, well over the 512-token window.
    text = " ".join(["word"] * 2000)
    chunks = chunk_text(text, max_tokens=512, overlap_tokens=64)
    assert len(chunks) > 1
    # Every chunk except the last should be exactly max_tokens long when
    # re-encoded (the last chunk may be shorter).
    import tiktoken
    enc = tiktoken.get_encoding("cl100k_base")
    for chunk in chunks[:-1]:
        assert len(enc.encode(chunk)) == 512


def test_custom_window_sizes_respected():
    text = " ".join(["word"] * 300)
    chunks = chunk_text(text, max_tokens=100, overlap_tokens=10)
    assert len(chunks) > 1
