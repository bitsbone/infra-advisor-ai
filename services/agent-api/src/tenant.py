"""Tenant-scoped identifiers for transient agent state.

Client-supplied session and conversation IDs are only routing hints. They must
never be used as Redis keys on their own because two authenticated users can
choose the same value. Hashing the JWT subject together with the routing hint
creates a stable, opaque namespace without placing user identifiers in Redis
keys or telemetry.
"""

import hashlib


def tenant_session_key(user_id: str, session_or_conversation_id: str) -> str:
    """Return an opaque key bound to both the JWT subject and client ID."""
    if not user_id or not session_or_conversation_id:
        raise ValueError("user and session identifiers are required")
    material = f"{user_id}\0{session_or_conversation_id}".encode("utf-8")
    return hashlib.sha256(material).hexdigest()
