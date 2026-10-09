"""Contract tests for the Kustomize overlays under k8s/overlays/.

Run with: uv run --with pytest --with pyyaml pytest k8s/tests
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
