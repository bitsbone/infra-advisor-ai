"""Executable deployment contracts for metadata-only MCP telemetry."""

import os
import subprocess
import sys
from pathlib import Path

import yaml


REPO_ROOT = Path(__file__).resolve().parents[3]


def test_mcp_deployment_forces_automatic_llmobs_content_capture_off():
    config = yaml.safe_load((REPO_ROOT / "k8s/mcp-server/configmap.yaml").read_text())
    deployment = yaml.safe_load((REPO_ROOT / "k8s/mcp-server/deployment.yaml").read_text())
    container = deployment["spec"]["template"]["spec"]["containers"][0]
    explicit_env = {item["name"]: item.get("value") for item in container["env"]}

    assert config["data"]["DD_LLMOBS_ENABLED"] == "false"
    assert explicit_env["DD_LLMOBS_ENABLED"] == "false"
    assert config["data"]["DD_TRACE_HTTP_CLIENT_TAG_QUERY_STRING"] == "false"


def test_mcp_image_defaults_automatic_llmobs_content_capture_off():
    dockerfile = (REPO_ROOT / "services/mcp-server/Dockerfile").read_text()

    assert "ENV DD_LLMOBS_ENABLED=false" in dockerfile
    assert "ENV DD_TRACE_HTTP_CLIENT_TAG_QUERY_STRING=false" in dockerfile


def test_ddtrace_runtime_keeps_llmobs_export_disabled():
    environment = os.environ.copy()
    environment.update({"DD_LLMOBS_ENABLED": "false", "DD_TRACE_ENABLED": "false"})

    result = subprocess.run(
        [sys.executable, "-c", "import ddtrace.auto; from ddtrace.llmobs import LLMObs; assert not LLMObs.enabled"],
        check=False,
        capture_output=True,
        text=True,
        env=environment,
    )

    assert result.returncode == 0, result.stderr
