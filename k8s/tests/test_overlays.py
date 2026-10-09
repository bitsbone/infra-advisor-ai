"""Contract tests for the Kustomize overlays under k8s/overlays/.

Run with: uv run --no-project --with pytest --with pyyaml --with httpx --with sqlalchemy pytest -q k8s/tests
Requires kubectl (for its built-in kustomize) on PATH.
"""

import subprocess
from pathlib import Path

import pytest
import yaml

K8S = Path(__file__).resolve().parents[1]
OVERLAYS = sorted(p.name for p in (K8S / "overlays").iterdir() if (p / "kustomization.yaml").exists())


def render(overlay: str) -> list[dict]:
    out = subprocess.run(
        ["kubectl", "kustomize", str(K8S / "overlays" / overlay)],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    return [doc for doc in yaml.safe_load_all(out) if doc]


def pod_specs(objects: list[dict]):
    for obj in objects:
        if obj["kind"] == "Deployment":
            yield obj, obj["spec"]["template"]["spec"]
        elif obj["kind"] == "CronJob":
            yield obj, obj["spec"]["jobTemplate"]["spec"]["template"]["spec"]


@pytest.fixture(scope="module", params=OVERLAYS)
def overlay(request):
    return request.param, render(request.param)


def test_overlays_exist():
    assert "prod" in OVERLAYS


def test_ghcr_workloads_have_pull_secret(overlay):
    _, objects = overlay
    for obj, spec in pod_specs(objects):
        if not any(c["image"].startswith("ghcr.io/") for c in spec["containers"]):
            continue
        assert {"name": "ghcr-pull-secret"} in spec.get("imagePullSecrets", []), obj["metadata"]["name"]


def test_prod_stays_in_prod_namespaces():
    allowed = {"", "infra-advisor", "kafka"}
    for obj in render("prod"):
        assert obj["metadata"].get("namespace", "") in allowed, obj["metadata"]["name"]


def test_env_tags_match_overlay(overlay):
    """Every env value an overlay sets (UST labels, DD_ENV, check-annotation tags) is the overlay name."""
    name, objects = overlay
    seen = []
    for obj in objects:
        rendered = yaml.safe_dump(obj)
        for label in ("tags.datadoghq.com/env: ", "DD_ENV: "):
            seen += [(obj["metadata"]["name"], line.split(label, 1)[1].strip("'\" ")) for line in rendered.splitlines() if label in line]
        seen += [(obj["metadata"]["name"], part.split('\\"', 1)[0]) for part in rendered.split('\\"env:')[1:]]
    assert seen
    assert {env for _, env in seen} == {name}, [s for s in seen if s[1] != name]


def test_overlays_include_dev():
    assert "dev" in OVERLAYS


@pytest.fixture(scope="module")
def dev():
    return render("dev")


def _by_kind_name(objects, kind, name):
    return next(o for o in objects if o["kind"] == kind and o["metadata"]["name"] == name)


def test_dev_stays_in_dev_namespace(dev):
    assert {o["metadata"].get("namespace", "") for o in dev} <= {"", "infra-advisor-dev"}
    assert "infra-advisor.svc" not in yaml.safe_dump_all(dev)


def test_dev_runs_no_kafka_scheduled_or_autoscaled_work(dev):
    kinds = {o["kind"] for o in dev}
    assert not kinds & {"Kafka", "KafkaTopic", "KafkaNodePool", "CronJob", "Job", "HorizontalPodAutoscaler"}
    for name in ("agent-api-config", "agent-api-dotnet-config"):
        assert _by_kind_name(dev, "ConfigMap", name)["data"]["KAFKA_ENABLED"] == "false"
    for o in dev:
        if o["kind"] == "Deployment":
            assert o["spec"].get("replicas", 1) == 1, o["metadata"]["name"]


def test_dev_isolates_media_and_public_hostname(dev):
    for name in ("agent-api-config", "agent-api-dotnet-config"):
        assert _by_kind_name(dev, "ConfigMap", name)["data"]["AZURE_STORAGE_MEDIA_CONTAINER"] == "chat-media-dev"
    auth = _by_kind_name(dev, "ConfigMap", "auth-api-config")["data"]
    assert auth["APP_BASE_URL"] == auth["ALLOWED_ORIGINS"] == "https://infra-advisor-ai-dev.bitsbone.com"
    ui = _by_kind_name(dev, "Service", "ui")
    assert "service.beta.kubernetes.io/azure-dns-label-name" not in ui["metadata"].get("annotations", {})


def test_dev_dbm_check_matches_prod_except_host_and_env(dev):
    key = "ad.datadoghq.com/postgres.checks"
    prod_check = _by_kind_name(render("prod"), "Deployment", "postgres")["spec"]["template"]["metadata"]["annotations"][key]
    dev_check = _by_kind_name(dev, "Deployment", "postgres")["spec"]["template"]["metadata"]["annotations"][key]
    expected = prod_check.replace('"infra-advisor-postgres"', '"infra-advisor-postgres-dev"').replace('"env:prod"', '"env:dev"')
    assert dev_check.strip() == expected.strip()


def _literal(path: Path, name: str):
    import ast

    for node in ast.walk(ast.parse(path.read_text())):
        targets = node.targets if isinstance(node, ast.Assign) else [node.target] if isinstance(node, ast.AnnAssign) else []
        if any(isinstance(t, ast.Name) and t.id == name for t in targets):
            return ast.literal_eval(node.value)
    raise AssertionError(f"{name} not found in {path}")


def test_seed_job_roles_match_auth_api():
    root = K8S.parent
    seed = _literal(K8S / "overlays/dev/seed/seed_dev.py", "JOB_ROLES")
    assert seed == _literal(root / "services/auth-api/src/database.py", "JOB_ROLES")


def test_seed_job_runs_in_dev_only():
    out = subprocess.run(["kubectl", "kustomize", str(K8S / "overlays/dev/seed")], check=True, capture_output=True, text=True).stdout
    assert {d["metadata"]["namespace"] for d in yaml.safe_load_all(out) if d} == {"infra-advisor-dev"}
