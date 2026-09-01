"""Datadog LLM Observability prompt tracking + prompt management.

DD_PROMPT_MANAGEMENT_ENABLED gates whether prompts are fetched from
Datadog's (preview) Prompt Registry at call time. Off by default: callers
always pass their own hardcoded string as fallback_template, which remains
the source of truth and the fail-open path — a registry outage or
misconfiguration must never prevent a prompt from resolving.

See scripts/seed_prompt_registry.py for the one-time script that pushes
today's hardcoded prompts into the registry as each prompt's v1.
"""

import hashlib
import logging
import os
from typing import Any

from ddtrace.llmobs import LLMObs

logger = logging.getLogger(__name__)

_PROMPT_MANAGEMENT_ENABLED = os.environ.get("DD_PROMPT_MANAGEMENT_ENABLED", "").lower() == "true"


def content_version(text: str) -> str:
    """Short content-hash version tag — matches agent-api-dotnet's ShortContentHash."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:8]


def fetch_prompt(prompt_id: str, fallback_template: str) -> tuple[str, dict[str, Any]]:
    """Resolve a prompt's effective template text plus its LLMObs annotation dict.

    Fail-open: any registry/network error (including DD_API_KEY missing,
    which LLMObs.get_prompt raises immediately for rather than falling back)
    falls through to fallback_template with source="fallback" tagged
    explicitly, mirroring observability.ai_guard's fail-open philosophy.
    """
    if _PROMPT_MANAGEMENT_ENABLED:
        try:
            managed = LLMObs.get_prompt(prompt_id, fallback=fallback_template)
            template = managed.template if isinstance(managed.template, str) else fallback_template
            return template, managed.to_annotation_dict()
        except Exception:
            logger.warning(
                "Prompt registry fetch failed for prompt_id=%s; using local fallback", prompt_id, exc_info=True
            )
    return fallback_template, {
        "id": prompt_id,
        "version": content_version(fallback_template),
        "template": fallback_template,
        "tags": {"source": "fallback"},
    }
