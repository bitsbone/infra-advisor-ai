"""LLM Observability helpers for agent-api.

Instrumentation strategy
------------------------
- LangChain chain/llm/tool calls  → auto-instrumented by ddtrace LangChain integration (>=2.9)
- MCP ClientSession.call_tool      → auto-instrumented by ddtrace MCP integration (>=3.11)
- Azure OpenAI chat completions    → auto-instrumented by ddtrace OpenAI integration (>=2.9)
- Orchestration spans              → explicit LLMObs.workflow/agent/task() in agent.py
- Faithfulness eval                → explicit LLMObs.task() wrapping auto-instrumented OpenAI call
"""

import asyncio
import logging
import os
import random
import time
from typing import Any

import httpx
from ddtrace.llmobs import LLMObs

logger = logging.getLogger(__name__)

# DogStatsD client for faithfulness gauge metric
try:
    from ddtrace.internal.dogstatsd import get_dogstatsd_client as _get_statsd

    statsd = _get_statsd(
        f"{os.environ.get('DD_AGENT_HOST', 'localhost')}:"
        f"{os.environ.get('DD_DOGSTATSD_PORT', '8125')}"
    )
except Exception:  # pragma: no cover
    statsd = None  # type: ignore

# ── Faithfulness evaluator system prompt ─────────────────────────────────────
# Kept separate so ddtrace sees a proper system role in the LLM call,
# not a user message that bundles instructions + context.
_EVAL_SYSTEM_PROMPT = (
    "You are a faithfulness evaluator. "
    "Given a context passage, a question, and an answer, rate how well "
    "the answer is grounded in the provided context. "
    "Reply with ONLY a decimal number between 0.0 (completely ungrounded) "
    "and 1.0 (fully grounded). No explanation, no extra text."
)


def enable_llm_obs() -> None:
    """Enable Datadog LLM Observability.

    Called once during FastAPI lifespan startup.  If DD_LLMOBS_ENABLED=true is
    already set in the environment (and ddtrace.auto was the first import),
    this is a no-op — LLMObs is already active.
    """
    ml_app = os.environ.get("DD_LLMOBS_ML_APP", "infra-advisor-ai")
    agentless = os.environ.get("DD_LLMOBS_AGENTLESS_ENABLED", "false").lower() == "true"
    try:
        LLMObs.enable(ml_app=ml_app, agentless_enabled=agentless)
        logger.info("LLMObs enabled ml_app=%s agentless=%s", ml_app, agentless)
    except Exception as exc:  # pragma: no cover
        logger.warning("LLMObs.enable() failed (non-fatal) error_type=%s", type(exc).__name__)


def tag_agent_run(
    span: Any,
    query: str,
    answer: str,
    query_domain: str,
    tools_called: list[str],
    cost_usd: float | None = None,
) -> None:
    """Annotate an agent span with bounded operational metadata only.

    Must be called while the LLMObs.agent() context manager is still open
    so that span is the active span — not after ainvoke() returns. Raw prompts
    and answers remain provider inputs/outputs and are never copied here.
    """
    try:
        LLMObs.annotate(
            span=span,
            tags={
                "query.domain": query_domain,
                "query.characters": str(len(query)),
                "response.characters": str(len(answer)),
                "agent.tools_called": ",".join(tools_called),
                **({"llm.cost_usd": str(cost_usd)} if cost_usd is not None else {}),
            },
        )
    except Exception as exc:  # pragma: no cover
        logger.debug("tag_agent_run failed (non-fatal) error_type=%s", type(exc).__name__)


async def _compute_faithfulness(
    query: str,
    context_chunks: list[str],
    answer: str,
    query_domain: str,
) -> None:
    """Faithfulness eval via gpt-4.1-mini.

    Runs as a background task (fire-and-forget) — zero added latency for users.
    Uses an explicit LLMObs.llm() span so the eval call appears as a separate
    sub-trace in LLM Observability, not bundled into the main agent span.

    The system/user message split ensures ddtrace classifies roles correctly:
    - system: evaluator instructions
    - user:   context + question + answer (the data to evaluate)
    """
    try:
        from openai import AsyncAzureOpenAI

        client = AsyncAzureOpenAI(
            azure_endpoint=os.environ.get("AZURE_OPENAI_ENDPOINT", ""),
            api_key=os.environ.get("AZURE_OPENAI_API_KEY", ""),
            api_version="2025-01-01-preview",
        )

        eval_model = os.environ.get("AZURE_OPENAI_EVAL_DEPLOYMENT", "gpt-4.1-mini")

        context_text = "\n---\n".join(context_chunks[:5]) if context_chunks else "(no context)"
        user_content = (
            f"Context:\n{context_text}\n\n"
            f"Question: {query}\n\n"
            f"Answer: {answer}"
        )

        # LLMObs.task() wraps the eval without conflicting with the auto-instrumented
        # OpenAI span — the AsyncAzureOpenAI call inside produces its own child LLM span
        # automatically, with token counts, model name, and i/o messages captured.
        with LLMObs.task("faithfulness-eval") as eval_span:
            response = await client.chat.completions.create(
                model=eval_model,
                messages=[
                    {"role": "system", "content": _EVAL_SYSTEM_PROMPT},
                    {"role": "user", "content": user_content},
                ],
                temperature=0,
                max_tokens=5,
            )

            raw = response.choices[0].message.content or ""
            score = max(0.0, min(1.0, float(raw.strip())))

            LLMObs.annotate(
                span=eval_span,
                tags={
                    "query.domain": query_domain,
                    "eval.faithfulness_score": str(score),
                    "eval.model": eval_model,
                },
            )

        logger.info(
            "faithfulness_score=%.3f domain=%s",
            score,
            query_domain,
        )

        if statsd is not None:
            statsd.gauge(
                "eval.faithfulness_score",
                score,
                tags=[f"query.domain:{query_domain}"],
            )

    except Exception as exc:
        logger.warning("faithfulness scoring failed (non-fatal) error_type=%s", type(exc).__name__)


def _feedback_payload(span_id: str, rating: str, submitter_id: str) -> dict[str, Any]:
    """Build Datadog's feedback event without evaluation-only join fields."""
    return {
        "data": {
            "type": "evaluation_metric",
            "attributes": {
                "metrics": [
                    {
                        "event_kind": "feedback",
                        "span_id": span_id,
                        "ml_app": os.environ.get("DD_LLMOBS_ML_APP", "infra-advisor-ai"),
                        "timestamp_ms": int(time.time() * 1000),
                        "metric_type": "categorical",
                        "label": "response_feedback",
                        "categorical_value": rating,
                        "assessment": "pass" if rating == "positive" else "fail",
                        "submitter": {"id": submitter_id, "type": "user"},
                    }
                ]
            },
        }
    }


async def submit_user_feedback(
    span_id: str,
    rating: str,
    submitter_id: str,
) -> bool:
    """Submit authenticated end-user feedback to Datadog LLM Observability.

    Datadog feedback events require a submitter and exactly one target. The
    response span is the most precise target, so trace and session identifiers
    deliberately stay out of this SDK call.
    """
    api_key = os.environ.get("DD_API_KEY")
    if not api_key:
        logger.warning("submit_user_feedback skipped because DD_API_KEY is not configured")
        return False

    site = os.environ.get("DD_SITE", "datadoghq.com")
    url = f"https://api.{site}/api/intake/llm-obs/v2/eval-metric"
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.post(
                url,
                headers={"DD-API-KEY": api_key},
                json=_feedback_payload(span_id, rating, submitter_id),
            )
            response.raise_for_status()
        logger.info("end_user_feedback submitted span_id=%s rating=%s", span_id, rating)
        return True
    except Exception as exc:
        logger.warning("submit_user_feedback failed (non-fatal) error_type=%s", type(exc).__name__)
        return False


def schedule_faithfulness_score(
    query: str,
    context_chunks: list[str],
    answer: str,
    query_domain: str = "general",
) -> None:
    """Sample and schedule non-blocking faithfulness scoring.

    The judge uses the same Azure OpenAI capacity as interactive traffic, so
    evaluating every answer can amplify a quota incident. Sampling preserves
    the demo signal while keeping user requests ahead of background analysis.
    """
    try:
        sample_rate = max(0.0, min(float(os.environ.get("EVAL_SAMPLE_RATE", "0.1")), 1.0))
        if random.random() >= sample_rate:
            logger.debug("faithfulness scoring skipped by sample rate")
            return
        loop = asyncio.get_event_loop()
        loop.create_task(
            _compute_faithfulness(query, context_chunks, answer, query_domain)
        )
    except Exception as exc:  # pragma: no cover
        logger.debug("schedule_faithfulness_score failed to schedule task error_type=%s", type(exc).__name__)
