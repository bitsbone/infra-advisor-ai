import os

# Unit tests import shared/search_upsert.py directly (not via function_app.py),
# so LLMObs.enable() never runs — LLMObs.embedding()/annotate() calls become
# no-ops (see shared/search_upsert.py's _safe_annotate). Explicitly disabling
# the tracer here just silences the harmless "failed to send traces to
# localhost:8126" warning ddtrace emits by default, matching agent-api's
# test setup convention.
os.environ.setdefault("DD_TRACE_ENABLED", "false")
os.environ.setdefault("DD_LLMOBS_ENABLED", "false")
