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

from .feature_flags import resolve_prompt_version

logger = logging.getLogger(__name__)

_PROMPT_MANAGEMENT_ENABLED = os.environ.get("DD_PROMPT_MANAGEMENT_ENABLED", "").lower() == "true"

# Last-resolved state per prompt_id, for the admin UI's read-only status
# panel (GET /admin/prompts/status). Updated on every fetch_prompt() call —
# in-memory only, reflects this pod's most recent resolution, not a
# durable log.
_LAST_RESOLVED: dict[str, dict[str, Any]] = {}


def get_last_resolved() -> list[dict[str, Any]]:
    """Snapshot of each prompt_id's most recent resolution, for the admin UI."""
    return list(_LAST_RESOLVED.values())


def content_version(text: str) -> str:
    """Short content-hash version tag — matches agent-api-dotnet's ShortContentHash."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:8]


def fetch_prompt(prompt_id: str, fallback_template: str) -> tuple[str, dict[str, Any]]:
    """Resolve a prompt's effective template text plus its LLMObs annotation dict.

    Fail-open: any registry/network error (including DD_API_KEY missing,
    which LLMObs.get_prompt raises immediately for rather than falling back)
    falls through to fallback_template with source="fallback" tagged
    explicitly, mirroring observability.ai_guard's fail-open philosophy.

    A Feature Flags-pinned version (see observability/feature_flags.py)
    takes precedence over the default env-resolve/latest behavior when set —
    this is what lets a prompt version be deployed per environment without
    a redeploy. See docs/src/content/docs/llm-engineering/monitoring/
    prompt-targeting.mdx.
    """
    if _PROMPT_MANAGEMENT_ENABLED:
        pinned_version = resolve_prompt_version(prompt_id)
        try:
            managed = (
                LLMObs.get_prompt(prompt_id, version=pinned_version, fallback=fallback_template)
                if pinned_version
                else LLMObs.get_prompt(prompt_id, fallback=fallback_template)
            )
            template = managed.template if isinstance(managed.template, str) else fallback_template
            annotation = managed.to_annotation_dict()
            _LAST_RESOLVED[prompt_id] = {
                "prompt_id": prompt_id,
                "backend": "python",
                "version": annotation.get("version"),
                "source": annotation.get("tags", {}).get("source", "registry"),
                "flag_value": pinned_version,
            }
            return template, annotation
        except Exception:
            logger.warning(
                "Prompt registry fetch failed for prompt_id=%s; using local fallback", prompt_id, exc_info=True
            )

    fallback_annotation = {
        "id": prompt_id,
        "version": content_version(fallback_template),
        "template": fallback_template,
        "tags": {"source": "fallback"},
    }
    _LAST_RESOLVED[prompt_id] = {
        "prompt_id": prompt_id,
        "backend": "python",
        "version": fallback_annotation["version"],
        "source": "fallback",
        "flag_value": 0,
    }
    return fallback_template, fallback_annotation
