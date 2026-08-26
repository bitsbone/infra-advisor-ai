"""Tests for the admin-managed password endpoint."""

import os
from unittest.mock import patch

import pytest
from fastapi import HTTPException

os.environ.setdefault("DATABASE_URL", "postgresql://test:test@localhost/test")
os.environ.setdefault("JWT_SECRET", "test-secret-for-unit-tests")

with patch("sqlalchemy.create_engine"), patch("database.init_db"):
    from fastapi.testclient import TestClient
    from auth import UserOut, require_admin
    from main import app

client = TestClient(app, raise_server_exceptions=False)

ADMIN = UserOut(
    id="admin-1",
    email="admin@datadoghq.com",
    is_admin=True,
    is_service_account=False,
    created_at="2026-01-01T00:00:00+00:00",
)

TARGET_USER = {
    "id": "user-1",
    "email": "user@datadoghq.com",
    "password_hash": "old-hash",
    "is_admin": False,
    "is_service_account": False,
    "created_at": "2026-01-01T00:00:00+00:00",
}


@pytest.fixture(autouse=True)
def admin_dependency():
    app.dependency_overrides[require_admin] = lambda: ADMIN
    yield
    app.dependency_overrides.clear()


def test_admin_sets_hashed_password_and_clears_reset_token():
    with (
        patch("main.get_user_by_id", return_value=TARGET_USER),
        patch("main.hash_password", return_value="new-hash") as mock_hash,
        patch("main.update_user", return_value={**TARGET_USER, "password_hash": "new-hash"}) as mock_update,
        patch("main.clear_reset_token") as mock_clear,
    ):
        response = client.put(
            "/admin/users/user-1/password",
            json={"new_password": "replacement-password"},
        )

    assert response.status_code == 200
    assert response.json() == {"updated": True}
    assert "replacement-password" not in response.text
    mock_hash.assert_called_once_with("replacement-password")
    mock_update.assert_called_once_with("user-1", password_hash="new-hash")
    mock_clear.assert_called_once_with("user-1")


def test_short_password_is_rejected_before_user_lookup():
    with patch("main.get_user_by_id") as mock_lookup:
        response = client.put(
            "/admin/users/user-1/password",
            json={"new_password": "short"},
        )

    assert response.status_code == 400
    assert "8 characters" in response.json()["detail"]
    mock_lookup.assert_not_called()


def test_password_over_bcrypt_byte_limit_is_rejected():
    response = client.put(
        "/admin/users/user-1/password",
        json={"new_password": "é" * 37},
    )

    assert response.status_code == 400
    assert "72 bytes" in response.json()["detail"]


def test_missing_user_returns_not_found_without_hashing_password():
    with (
        patch("main.get_user_by_id", return_value=None),
        patch("main.hash_password") as mock_hash,
    ):
        response = client.put(
            "/admin/users/missing/password",
            json={"new_password": "replacement-password"},
        )

    assert response.status_code == 404
    assert response.json()["detail"] == "User not found"
    mock_hash.assert_not_called()


def test_non_admin_is_forbidden():
    def reject_non_admin():
        raise HTTPException(status_code=403, detail="Admin access required")

    app.dependency_overrides[require_admin] = reject_non_admin
    response = client.put(
        "/admin/users/user-1/password",
        json={"new_password": "replacement-password"},
    )

    assert response.status_code == 403
    assert response.json()["detail"] == "Admin access required"
