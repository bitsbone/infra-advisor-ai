"""fetch_prompt() fail-open behavior — prompt tracking + prompt management."""

import os
import sys
from unittest.mock import patch

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

from observability import prompts as prompts_module


def test_disabled_by_default_returns_fallback_without_calling_get_prompt():
    with patch.object(prompts_module, "_PROMPT_MANAGEMENT_ENABLED", False), \
         patch.object(prompts_module.LLMObs, "get_prompt") as mock_get_prompt:
        template, meta = prompts_module.fetch_prompt("router", "local fallback text")

    mock_get_prompt.assert_not_called()
    assert template == "local fallback text"
    assert meta["id"] == "router"
    assert meta["template"] == "local fallback text"
    assert meta["tags"] == {"source": "fallback"}


def test_get_prompt_exception_falls_back_cleanly():
    with patch.object(prompts_module, "_PROMPT_MANAGEMENT_ENABLED", True), \
         patch.object(prompts_module.LLMObs, "get_prompt", side_effect=RuntimeError("registry unreachable")):
        template, meta = prompts_module.fetch_prompt("router", "local fallback text")

    assert template == "local fallback text"
    assert meta["tags"] == {"source": "fallback"}


def test_enabled_and_successful_fetch_uses_managed_prompt():
    managed = type(
        "FakeManagedPrompt",
        (),
        {
            "template": "registry-managed text",
            "to_annotation_dict": lambda self: {"id": "router", "version": "3", "template": "registry-managed text"},
        },
    )()

    with patch.object(prompts_module, "_PROMPT_MANAGEMENT_ENABLED", True), \
         patch.object(prompts_module.LLMObs, "get_prompt", return_value=managed):
        template, meta = prompts_module.fetch_prompt("router", "local fallback text")

    assert template == "registry-managed text"
    assert meta["version"] == "3"


def test_content_version_is_stable_and_short():
    v1 = prompts_module.content_version("some prompt text")
    v2 = prompts_module.content_version("some prompt text")
    v3 = prompts_module.content_version("different text")

    assert v1 == v2
    assert v1 != v3
    assert len(v1) == 8


def test_business_development_prompt_requires_contract_awards_query():
    from agent import _BUSINESS_DEVELOPMENT_TOOL_GUIDANCE, _SPECIALIST_SYSTEM_PROMPTS

    prompt = _SPECIALIST_SYSTEM_PROMPTS["business_development"]

    assert _BUSINESS_DEVELOPMENT_TOOL_GUIDANCE in prompt
    assert "query=\"heavy civil construction contracts\"" in prompt
    assert "Never call get_contract_awards with only optional filters" in prompt


def test_business_development_guidance_is_added_to_stale_managed_prompt():
    from agent import _BUSINESS_DEVELOPMENT_TOOL_GUIDANCE, _resolve_specialist_prompt

    with patch(
        "agent.fetch_prompt",
        return_value=("stale managed business-development prompt", {"id": "specialist-business_development"}),
    ):
        prompt, meta = _resolve_specialist_prompt("business_development")

    assert prompt == f"stale managed business-development prompt\n\n{_BUSINESS_DEVELOPMENT_TOOL_GUIDANCE}"
    assert meta["template"] == prompt
    assert len(meta["version"]) == 8
