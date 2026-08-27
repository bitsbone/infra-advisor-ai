"""Conversation persistence coverage for additive chat artifacts."""

from types import SimpleNamespace

import conversations


class FakeSession:
    def __init__(self):
        self.rows = []
        self.committed = False
        self.closed = False

    def add(self, row):
        self.rows.append(row)

    def query(self, _model):
        return self

    def filter(self, *_criteria):
        return self

    def with_for_update(self):
        return self

    def first(self):
        return SimpleNamespace(updated_at=None)

    def execute(self, _statement):
        return None

    def commit(self):
        self.committed = True

    def rollback(self):
        raise AssertionError("persistence should not roll back")

    def refresh(self, _row):
        return None

    def close(self):
        self.closed = True


def test_save_messages_puts_artifacts_only_on_assistant(monkeypatch):
    session = FakeSession()
    artifact = {"kind": "procurement_opportunities", "schema_version": "1.0", "items": [], "meta": {}}
    monkeypatch.setattr(conversations, "_get_db", lambda: session)

    conversations.save_messages(
        "550e8400-e29b-41d4-a716-446655440000",
        "sanitized test question",
        "sanitized test answer",
        [],
        None,
        None,
        user_id="test-user-id",
        artifacts=[artifact],
    )

    assert session.committed and session.closed
    assert session.rows[0].artifacts == []
    assert session.rows[1].artifacts == [artifact]


def test_message_serialization_defaults_legacy_artifacts_to_empty():
    row = SimpleNamespace(
        id="message-id",
        conversation_id="conversation-id",
        role="assistant",
        content="answer",
        sources=[],
        steps=[],
        attachments=[],
        artifacts=None,
        trace_id=None,
        span_id=None,
        created_at=None,
    )

    assert conversations._msg_to_dict(row)["artifacts"] == []


def test_conversation_title_is_normalized_for_attachment_only_clients(monkeypatch):
    session = FakeSession()
    monkeypatch.setattr(conversations, "_get_db", lambda: session)

    conversations.create_conversation("user-id", "   ")

    assert session.rows[0].title == "New Conversation"
