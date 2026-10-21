"""Seed the dev namespace with test users and sample conversations.

Idempotent: safe to re-run after every deploy. Never reads or copies prod
data. Creates, through auth-api's admin API:

- the shared synthetic-test service account (SYNTHETIC_USER_EMAIL), whose
  password is reset to SYNTHETIC_USER_PASSWORD on every run so it stays in
  sync with the Datadog Synthetics secure variable;
- one demo service account per job_role (demo-<role>@infra-advisor.test,
  same password), for prompt-flag targeting tests;

then inserts two sample conversations per demo user directly into Postgres,
with deterministic ids so re-runs don't duplicate them. The conversations and
messages tables are created by agent-api at startup.

Runs as the seed-dev Job (k8s/overlays/dev/seed) with the agent-api image.
With SEED_SYNTHETIC_ONLY=1 it only creates/updates the synthetic account; that
is how `make create-synthetic-user ENV=prod` gives prod the same account
without touching any other prod data.
"""

import getpass
import os
import re
import sys
import time
import uuid

import httpx
from sqlalchemy import create_engine, text

AUTH_API_URL = os.environ.get("AUTH_API_URL", "http://auth-api:8002")
SYNTHETIC_EMAIL = os.environ["SYNTHETIC_USER_EMAIL"].strip().lower()
SYNTHETIC_PASSWORD = os.environ["SYNTHETIC_USER_PASSWORD"]
SYNTHETIC_ONLY = os.environ.get("SEED_SYNTHETIC_ONLY") == "1"

# Must match JOB_ROLES in services/auth-api/src/database.py (k8s/tests checks).
JOB_ROLES = [
    "Civil Engineer",
    "Water & Energy Analyst",
    "Business Development Manager",
    "Contracts Administrator",
    "Program Director",
]
SYNTHETIC_JOB_ROLE = "Civil Engineer"
SEED_NAMESPACE = uuid.UUID("6f1d3c52-8a8e-4c1e-9a52-0d1d0e7f5a11")

SAMPLE_CONVERSATIONS = [
    (
        "Bridge deck condition near Austin",
        "Which bridges near Austin, TX have a deck condition rating of 4 or lower?",
        "Sample answer (seeded): ask this again to get a live answer from the NBI tools.",
    ),
    (
        "ERCOT load outlook",
        "Summarise this week's ERCOT load forecast and any grid stress signals.",
        "Sample answer (seeded): ask this again to get a live answer from the ERCOT tools.",
    ),
]


def slug(role: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", role.lower()).strip("-")


def wait_for(client: httpx.Client, url: str, attempts: int = 60) -> None:
    for _ in range(attempts):
        try:
            if client.get(url).status_code == 200:
                return
        except httpx.HTTPError:
            pass
        time.sleep(5)
    sys.exit(f"{url} never became ready")


def ensure_user(client: httpx.Client, headers: dict, email: str, password: str, job_role: str) -> str:
    users = {u["email"]: u for u in client.get("/admin/users", headers=headers).raise_for_status().json()}
    if email in users:
        user_id = users[email]["id"]
        client.put(f"/admin/users/{user_id}/password", headers=headers, json={"new_password": password}).raise_for_status()
        action = "updated"
    else:
        created = client.post(
            "/admin/users",
            headers=headers,
            json={"email": email, "password": password, "is_admin": False, "is_service_account": True},
        ).raise_for_status()
        user_id = created.json()["id"]
        action = "created"
    client.patch(f"/admin/users/{user_id}", headers=headers, json={"job_role": job_role}).raise_for_status()
    print(f"{action} {email} ({job_role})")
    return user_id


def seed_conversations(user_ids: list[str]) -> None:
    engine = create_engine(os.environ["DATABASE_URL"], pool_pre_ping=True)
    with engine.begin() as conn:
        for user_id in user_ids:
            for title, question, answer in SAMPLE_CONVERSATIONS:
                conv_id = uuid.uuid5(SEED_NAMESPACE, f"{user_id}:{title}")
                conn.execute(
                    text(
                        "INSERT INTO conversations (id, user_id, title, model, backend) "
                        "VALUES (:id, :user_id, :title, 'gpt-5.4-mini', 'python') ON CONFLICT (id) DO NOTHING"
                    ),
                    {"id": conv_id, "user_id": user_id, "title": title},
                )
                for role, content in (("user", question), ("assistant", answer)):
                    conn.execute(
                        text(
                            "INSERT INTO messages (id, conversation_id, role, content) "
                            "VALUES (:id, :conv_id, :role, :content) ON CONFLICT (id) DO NOTHING"
                        ),
                        {"id": uuid.uuid5(conv_id, role), "conv_id": conv_id, "role": role, "content": content},
                    )
    print(f"seeded {len(SAMPLE_CONVERSATIONS)} conversations for {len(user_ids)} demo users")


def admin_credentials() -> tuple[str, str]:
    """ADMIN_EMAIL/ADMIN_PASSWORD, else the bootstrap admin. The bootstrap
    account only exists if the database was empty when auth-api first started
    with it set, so prod may need a different admin; without ADMIN_PASSWORD in
    an interactive shell, the password is prompted for instead of being passed
    on the command line."""
    email = os.environ.get("ADMIN_EMAIL") or os.environ.get("BOOTSTRAP_ADMIN_EMAIL", "")
    password = os.environ.get("ADMIN_PASSWORD") or (
        os.environ.get("BOOTSTRAP_ADMIN_PASSWORD", "") if not os.environ.get("ADMIN_EMAIL") else ""
    )
    if not password and sys.stdin.isatty():
        password = getpass.getpass(f"Admin password for {email} at {AUTH_API_URL}: ")
    if not email or not password:
        sys.exit("No admin credentials: set ADMIN_EMAIL (and ADMIN_PASSWORD or answer the prompt), or BOOTSTRAP_ADMIN_*")
    return email, password


def main() -> None:
    admin_email, admin_password = admin_credentials()
    with httpx.Client(base_url=AUTH_API_URL, timeout=30) as client:
        wait_for(client, "/readyz")
        login = client.post("/login", json={"email": admin_email, "password": admin_password})
        if login.status_code == 401:
            sys.exit(f"Admin login failed for {admin_email} at {AUTH_API_URL} (401). Use an account that is an admin there: ADMIN_EMAIL=<you> make create-synthetic-user ENV=<env>")
        token = login.raise_for_status().json()["token"]
        headers = {"Authorization": f"Bearer {token}"}

        ensure_user(client, headers, SYNTHETIC_EMAIL, SYNTHETIC_PASSWORD, SYNTHETIC_JOB_ROLE)
        if SYNTHETIC_ONLY:
            return
        demo_ids = [
            ensure_user(client, headers, f"demo-{slug(role)}@infra-advisor.test", SYNTHETIC_PASSWORD, role)
            for role in JOB_ROLES
        ]
    seed_conversations(demo_ids)


if __name__ == "__main__":
    main()
