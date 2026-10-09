"""seed_dev.py against an in-memory fake of auth-api's admin API.

Run with: uv run --no-project --with pytest --with pyyaml --with httpx --with sqlalchemy pytest -q k8s/tests
"""

import importlib.util
import json
import sys
import uuid
from pathlib import Path

import httpx
import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "overlays/dev/seed/seed_dev.py"
ENV = {
    "BOOTSTRAP_ADMIN_EMAIL": "admin@example.test",
    "BOOTSTRAP_ADMIN_PASSWORD": "admin-password",
    "SYNTHETIC_USER_EMAIL": "Synthetics@Infra-Advisor.test",
    "SYNTHETIC_USER_PASSWORD": "synthetic-password",
}


class FakeAuthApi:
    def __init__(self):
        self.users: dict[str, dict] = {}
        self.calls: list[tuple[str, str]] = []

    def handler(self, request: httpx.Request) -> httpx.Response:
        path, method = request.url.path, request.method
        self.calls.append((method, path))
        body = json.loads(request.content) if request.content else {}
        if path == "/readyz":
            return httpx.Response(200)
        if path == "/login":
            return httpx.Response(200, json={"token": "t"})
        assert request.headers["Authorization"] == "Bearer t"
        if path == "/admin/users" and method == "GET":
            return httpx.Response(200, json=list(self.users.values()))
        if path == "/admin/users" and method == "POST":
            assert body["email"] not in {u["email"] for u in self.users.values()}
            assert body["is_service_account"] is True and body["is_admin"] is False
            user = {"id": str(uuid.uuid4()), "email": body["email"], "password": body["password"], "job_role": None}
            self.users[user["id"]] = user
            return httpx.Response(201, json=user)
        user_id = path.split("/")[3]
        if path.endswith("/password"):
            self.users[user_id]["password"] = body["new_password"]
            return httpx.Response(200, json={})
        self.users[user_id]["job_role"] = body["job_role"]
        return httpx.Response(200, json=self.users[user_id])


@pytest.fixture
def run_seed(monkeypatch):
    fake = FakeAuthApi()
    real_client = httpx.Client
    monkeypatch.setattr(httpx, "Client", lambda **kw: real_client(transport=httpx.MockTransport(fake.handler), **kw))
    for key, value in ENV.items():
        monkeypatch.setenv(key, value)

    def run(synthetic_only: bool):
        monkeypatch.setenv("SEED_SYNTHETIC_ONLY", "1" if synthetic_only else "0")
        spec = importlib.util.spec_from_file_location("seed_dev", SCRIPT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        seeded = []
        monkeypatch.setattr(module, "seed_conversations", seeded.extend)
        module.main()
        return module, seeded

    return fake, run


def test_seed_creates_synthetic_and_one_demo_user_per_role(run_seed):
    fake, run = run_seed
    module, seeded = run(synthetic_only=False)
    by_email = {u["email"]: u for u in fake.users.values()}
    assert by_email["synthetics@infra-advisor.test"]["password"] == "synthetic-password"
    assert {u["job_role"] for e, u in by_email.items() if e.startswith("demo-")} == set(module.JOB_ROLES)
    assert len(by_email) == 1 + len(module.JOB_ROLES)
    assert len(seeded) == len(module.JOB_ROLES)


def test_seed_is_idempotent_and_resyncs_the_synthetic_password(run_seed):
    fake, run = run_seed
    run(synthetic_only=False)
    synthetic = next(u for u in fake.users.values() if u["email"].startswith("synthetics@"))
    synthetic["password"] = "drifted"
    run(synthetic_only=False)
    assert len(fake.users) == 6
    assert synthetic["password"] == "synthetic-password"


def test_synthetic_only_touches_nothing_else(run_seed):
    fake, run = run_seed
    _, seeded = run(synthetic_only=True)
    assert [u["email"] for u in fake.users.values()] == ["synthetics@infra-advisor.test"]
    assert seeded == []
