"""OpenFeature-backed prompt version pinning via Datadog Feature Flags.

Each managed prompt_id has a corresponding integer Feature Flag named
`prompt-version.<prompt_id>`, created in Datadog's Feature Flags product
(not Prompt Management's own env_ids/targeting-rule tab — see
docs/src/content/docs/llm-engineering/monitoring/prompt-targeting.mdx for
why). A targeting rule can pin a specific registry version per environment
(matched on the `env` attribute below); the flag's default value, `0`, is
the "no override" sentinel — fetch_prompt() falls through to its existing
env-resolve/fallback behavior unchanged.

Fails open on any error, exactly like the rest of this package: a Feature
Flags outage or misconfiguration must never block a prompt from resolving.
"""

import logging
import os

from ddtrace.openfeature import DataDogProvider
from openfeature import api
from openfeature.evaluation_context import EvaluationContext

logger = logging.getLogger(__name__)

_DOMAIN = "infra-advisor-prompts"
_provider_set = False


def _ensure_provider() -> None:
    global _provider_set
    if _provider_set:
        return
    try:
        api.set_provider(DataDogProvider(), _DOMAIN)
    except Exception:
        logger.warning("Failed to register Datadog OpenFeature provider", exc_info=True)
    _provider_set = True


def resolve_prompt_version(prompt_id: str) -> int:
    """Return the pinned registry version for prompt_id, or 0 if unset/unavailable."""
    _ensure_provider()
    try:
        client = api.get_client(_DOMAIN)
        context = EvaluationContext(attributes={"env": os.environ.get("DD_ENV", "")})
        return client.get_integer_value(f"prompt-version.{prompt_id}", 0, context)
    except Exception:
        logger.warning("Prompt-version flag evaluation failed for prompt_id=%s", prompt_id, exc_info=True)
        return 0
