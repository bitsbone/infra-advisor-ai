"""Redis-backed session memory for the InfraAdvisor agent.

Key pattern : infra-advisor:session:{tenant-scoped SHA-256 key}:memory
Window      : last 10 human/AI exchange pairs
TTL         : 86400 seconds (24 hours), refreshed on every write
"""

import json
import logging
import os
from typing import Any

import redis

logger = logging.getLogger(__name__)

_SESSION_PREFIX = "infra-advisor:session"
_MEMORY_SUFFIX = "memory"
_SESSION_TTL = 86_400  # 24 hours
_WINDOW_SIZE = 10  # exchange pairs to retain


def _redis_client() -> redis.Redis:
    host = os.environ.get("REDIS_HOST", "redis.infra-advisor.svc.cluster.local")
    port = int(os.environ.get("REDIS_PORT", "6379"))
    password = os.environ.get("REDIS_PASSWORD") or None
    return redis.Redis(host=host, port=port, password=password, decode_responses=True)


get_redis = _redis_client  # public alias for use outside this module


def _memory_key(session_id: str) -> str:
    # Callers pass tenant_session_key(jwt_sub, client_id), never a raw client
    # session/conversation ID. Keeping key assembly here simple lets tests mock
    # Redis without weakening the HTTP authorization boundary in main.py.
    return f"{_SESSION_PREFIX}:{session_id}:{_MEMORY_SUFFIX}"


def load_history(session_id: str) -> list[dict[str, Any]]:
    """Return the conversation history list for this session.

    Each entry is ``{"role": "human"|"ai", "content": "..."}``
    Returns an empty list if the session does not exist or Redis is unavailable.
    """
    key = _memory_key(session_id)
    try:
        client = _redis_client()
        raw = client.get(key)
        if raw is None:
            return []
        history: list[dict[str, Any]] = json.loads(raw)
        return history[-_WINDOW_SIZE * 2 :]  # keep last N pairs (2 messages per pair)
    except Exception as exc:
        logger.warning("load_history failed error_type=%s", type(exc).__name__)
        return []


def save_history(session_id: str, history: list[dict[str, Any]]) -> None:
    """Persist the conversation history and refresh TTL.

    Truncates to the last ``_WINDOW_SIZE`` exchange pairs before saving.
    """
    key = _memory_key(session_id)
    # Keep last N pairs (2 messages per pair: human + ai)
    trimmed = history[-_WINDOW_SIZE * 2 :]
    try:
        client = _redis_client()
        client.setex(key, _SESSION_TTL, json.dumps(trimmed))
    except Exception as exc:
        logger.warning("save_history failed error_type=%s", type(exc).__name__)


def append_exchange(session_id: str, human_message: str, ai_message: str) -> None:
    """Append a human/AI exchange to the session history and refresh TTL."""
    history = load_history(session_id)
    history.append({"role": "human", "content": human_message})
    history.append({"role": "ai", "content": ai_message})
    save_history(session_id, history)


def append_exchange_with_attachments(
    session_id: str,
    human_message: str,
    ai_message: str,
    attachments: list[dict[str, Any]] | None = None,
) -> None:
    """Same as append_exchange, but records attachment metadata (url/kind/
    mime_type/size_bytes) alongside the human turn — for display purposes
    only when a conversation is reloaded. Attachments are NOT re-sent as
    multimodal content on subsequent turns (see agent.py); this is purely a
    record of what was attached to this specific turn. Old entries without
    an "attachments" key still round-trip fine — nothing reads it as
    required.
    """
    history = load_history(session_id)
    human_entry: dict[str, Any] = {"role": "human", "content": human_message}
    if attachments:
        human_entry["attachments"] = attachments
    history.append(human_entry)
    history.append({"role": "ai", "content": ai_message})
    save_history(session_id, history)


def clear_session(session_id: str) -> bool:
    """Delete session memory from Redis.  Returns True if key was deleted."""
    key = _memory_key(session_id)
    try:
        client = _redis_client()
        deleted = client.delete(key)
        return bool(deleted)
    except Exception as exc:
        logger.warning("clear_session failed error_type=%s", type(exc).__name__)
        return False


def history_to_langchain_messages(history: list[dict[str, Any]]) -> list[Any]:
    """Convert stored history to LangChain HumanMessage/AIMessage objects."""
    from langchain_core.messages import AIMessage, HumanMessage

    messages: list[Any] = []
    for entry in history:
        role = entry.get("role", "")
        content = entry.get("content", "")
        if role == "human":
            messages.append(HumanMessage(content=content))
        elif role == "ai":
            messages.append(AIMessage(content=content))
    return messages


_MODEL_SUFFIX = "model"
_DEFAULT_MODEL = "gpt-4.1-mini"


def get_session_model(session_id: str) -> str:
    """Return the last-used deployment name for this session, or the default."""
    key = f"{_SESSION_PREFIX}:{session_id}:{_MODEL_SUFFIX}"
    try:
        client = _redis_client()
        val = client.get(key)
        return val if val else _DEFAULT_MODEL
    except Exception as exc:
        logger.warning("get_session_model failed error_type=%s", type(exc).__name__)
        return _DEFAULT_MODEL


def set_session_model(session_id: str, model: str) -> None:
    """Persist the chosen deployment name for this session (same TTL as history)."""
    key = f"{_SESSION_PREFIX}:{session_id}:{_MODEL_SUFFIX}"
    try:
        client = _redis_client()
        client.setex(key, _SESSION_TTL, model)
    except Exception as exc:
        logger.warning("set_session_model failed error_type=%s", type(exc).__name__)
