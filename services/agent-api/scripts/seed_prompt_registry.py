"""One-off script: seed Datadog's Prompt Registry with today's hardcoded
prompts as each prompt's v1, so LLMObs.get_prompt() has something real to
resolve once DD_PROMPT_MANAGEMENT_ENABLED=true is turned on.

Not part of app startup — run manually, same posture as
services/adf-functions/scripts/create_search_index.py. Safe to re-run:
an existing prompt_id is skipped (logged), not overwritten — use
LLMObs.create_prompt_version() directly if you want to push a new version.

Also seeds the one .NET (agent-api-dotnet) prompt, since prompt creation is
registry-side, not language-specific, and that service has no native SDK
to run this itself. Its content is extracted directly from Program.cs
(see _extract_dotnet_system_prompt) rather than requiring an operator to
copy-paste it into an env var — a prior version of this script did that,
and nothing enforced the pasted copy stayed in sync with the real constant.

Usage:
    uv run python scripts/seed_prompt_registry.py
"""

import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

from ddtrace.llmobs import LLMObs
from ddtrace.llmobs.types import PromptConflictError

from agent import _ROUTER_SYSTEM_TEXT, _SPECIALIST_SYSTEM_PROMPTS, _IMAGE_DESCRIPTION_PROMPT
from observability.llm_obs import _EVAL_SYSTEM_PROMPT

_DOTNET_PROGRAM_CS = os.path.join(
    os.path.dirname(__file__), "..", "..", "agent-api-dotnet", "Program.cs"
)


def _extract_dotnet_system_prompt(program_cs_path: str) -> str | None:
    """Extract AgentSystemPrompt's concatenated string literal directly from
    Program.cs. The declaration is a C# `"..." + "..." + ...;` chain, so the
    first semicolon isn't reliable as the end boundary — the prompt text
    itself contains real semicolons as ordinary punctuation. Instead find a
    closing quote immediately followed by the statement-terminating
    semicolon (allowing only whitespace between), which only matches at the
    true end of the declaration.
    """
    try:
        with open(program_cs_path, encoding="utf-8") as f:
            src = f.read()
    except FileNotFoundError:
        return None

    marker = "const string AgentSystemPrompt ="
    if marker not in src:
        return None
    start = src.index(marker)
    end_match = re.search(r'"\s*;', src[start:])
    if not end_match:
        return None
    block = src[start : start + end_match.end()]

    literals = re.findall(r'"((?:[^"\\]|\\.)*)"', block)
    return "".join(s.replace('\\"', '"').replace("\\n", "\n").replace("\\\\", "\\") for s in literals)


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

    dotnet_prompt = _extract_dotnet_system_prompt(_DOTNET_PROGRAM_CS)
    if dotnet_prompt:
        _seed("infra-advisor-system-prompt", dotnet_prompt, "InfraAdvisor .NET agent system prompt")
    else:
        print(
            "Skipping infra-advisor-system-prompt (.NET) — could not find/parse "
            f"AgentSystemPrompt in {_DOTNET_PROGRAM_CS}"
        )


if __name__ == "__main__":
    main()
