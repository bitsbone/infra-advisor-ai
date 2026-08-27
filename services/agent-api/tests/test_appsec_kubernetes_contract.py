from __future__ import annotations

from pathlib import Path

import yaml


REPO_ROOT = Path(__file__).resolve().parents[3]


class UniqueKeyLoader(yaml.SafeLoader):
    """Fail instead of silently accepting duplicate Kubernetes YAML keys."""


def _construct_unique_mapping(loader, node, deep=False):
    mapping = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        if key in mapping:
            raise yaml.constructor.ConstructorError(
                "while constructing a mapping",
                node.start_mark,
                f"duplicate key: {key}",
                key_node.start_mark,
            )
        mapping[key] = loader.construct_object(value_node, deep=deep)
    return mapping


UniqueKeyLoader.add_constructor(
    yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG,
    _construct_unique_mapping,
)


def _load(relative_path: str):
    source = (REPO_ROOT / relative_path).read_text()
    return yaml.load(source, Loader=UniqueKeyLoader)


def _config_data(relative_path: str) -> dict[str, str]:
    return _load(relative_path)["data"]


def test_python_agent_enables_app_and_api_protection_with_apm():
    deployment = _load("k8s/agent-api/deployment.yaml")
    config = _config_data("k8s/agent-api/configmap.yaml")

    labels = deployment["spec"]["template"]["metadata"]["labels"]
    assert labels["admission.datadoghq.com/enabled"] == "true"
    assert config["DD_APPSEC_ENABLED"] == "true"
    assert config["DD_API_SECURITY_ENABLED"] == "true"
    assert config["DD_TRACE_ENABLED"] == "true"
    assert "DD_APM_TRACING_ENABLED" not in config


def test_dotnet_agent_enables_security_without_duplicate_apm_tracing():
    deployment = _load("k8s/agent-api-dotnet/deployment.yaml")
    config = _config_data("k8s/agent-api-dotnet/configmap.yaml")

    pod_metadata = deployment["spec"]["template"]["metadata"]
    assert pod_metadata["labels"]["admission.datadoghq.com/enabled"] == "true"
    assert pod_metadata["annotations"]["admission.datadoghq.com/dotnet-lib.version"] == "3.44.0"
    assert config["DD_APPSEC_ENABLED"] == "true"
    assert config["DD_API_SECURITY_ENABLED"] == "true"
    assert config["DD_APM_TRACING_ENABLED"] == "false"
    assert "DD_TRACE_ENABLED" not in config
    assert config["OTEL_EXPORTER_OTLP_ENDPOINT"].endswith(":4318")


def test_cluster_agent_contract_supports_pinned_security_injection():
    agent = _load("datadog/datadog-agent.yaml")
    features = agent["spec"]["features"]
    admission = features["admissionController"]
    target = features["apm"]["instrumentation"]["targets"][0]

    assert admission == {"enabled": True, "mutateUnlabelled": False}
    assert target["namespaceSelector"]["matchLabels"] == {
        "kubernetes.io/metadata.name": "infra-advisor"
    }
    assert target["podSelector"]["matchLabels"] == {"app": "agent-api-dotnet"}
    assert set(target["ddTraceVersions"]) == {"dotnet"}
    assert target["ddTraceVersions"]["dotnet"] == "3.44.0"
    assert features["asm"]["threats"]["enabled"] is True
