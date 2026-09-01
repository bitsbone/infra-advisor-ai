"""One-off script: seed Datadog's Prompt Registry with today's hardcoded
prompts as each prompt's v1, so LLMObs.get_prompt() has something real to
resolve once DD_PROMPT_MANAGEMENT_ENABLED=true is turned on.

Not part of app startup — run manually, same posture as
services/adf-functions/scripts/create_search_index.py. Safe to re-run:
an existing prompt_id is skipped (logged), not overwritten — use
LLMObs.create_prompt_version() directly if you want to push a new version.

Also seeds the one .NET (agent-api-dotnet) prompt, since prompt creation is
registry-side, not language-specific, and that service has no native SDK
to run this itself.

Usage:
    uv run python scripts/seed_prompt_registry.py
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

from ddtrace.llmobs import LLMObs
from ddtrace.llmobs.types import PromptConflictError

from agent import _ROUTER_SYSTEM_TEXT, _SPECIALIST_SYSTEM_PROMPTS, _IMAGE_DESCRIPTION_PROMPT
from observability.llm_obs import _EVAL_SYSTEM_PROMPT


def _seed(prompt_id: str, template_text: str, title: str) -> None:
    try:
        LLMObs.create_prompt(
            prompt_id,
            [{"role": "system", "content": template_text}],
            title=title,
            user_version="v1",
        )
        print(f"created {prompt_id!r} (v1)")
    except PromptConflictError:
        print(f"{prompt_id!r} already exists — skipping")


def main() -> None:
    LLMObs.enable(ml_app=os.environ.get("DD_LLMOBS_ML_APP", "infra-advisor-ai"))

    _seed("router", _ROUTER_SYSTEM_TEXT, "InfraAdvisor router prompt")
    for name, text in _SPECIALIST_SYSTEM_PROMPTS.items():
        _seed(f"specialist-{name}", text, f"InfraAdvisor {name} specialist prompt")
    _seed("describe-image", _IMAGE_DESCRIPTION_PROMPT, "InfraAdvisor image-description prompt")
    _seed("faithfulness-eval", _EVAL_SYSTEM_PROMPT, "InfraAdvisor faithfulness evaluator prompt")

    # .NET's single system prompt — read from an explicit env var rather than
    # parsing Program.cs, so this script never silently seeds a stale copy.
    dotnet_prompt = os.environ.get("DOTNET_AGENT_SYSTEM_PROMPT")
    if dotnet_prompt:
        _seed("infra-advisor-system-prompt", dotnet_prompt, "InfraAdvisor .NET agent system prompt")
    else:
        print(
            "Skipping infra-advisor-system-prompt (.NET) — set DOTNET_AGENT_SYSTEM_PROMPT "
            "to the exact AgentSystemPrompt string from agent-api-dotnet/Program.cs to seed it."
        )


if __name__ == "__main__":
    main()
