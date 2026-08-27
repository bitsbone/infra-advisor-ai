"""Health endpoint contract tests."""

import os
from unittest.mock import patch

os.environ.setdefault("DATABASE_URL", "postgresql://test:test@localhost/test")
os.environ.setdefault("JWT_SECRET", "test-secret-for-unit-tests")

with patch("sqlalchemy.create_engine"), patch("database.init_db"):
    from fastapi.testclient import TestClient
    from main import app


client = TestClient(app, raise_server_exceptions=False)


def test_liveness_is_shallow_and_successful():
    response = client.get("/livez")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_readiness_is_successful_after_startup():
    response = client.get("/readyz")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_legacy_health_endpoint_remains_compatible():
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}
