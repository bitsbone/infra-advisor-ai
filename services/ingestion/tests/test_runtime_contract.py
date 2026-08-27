from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

import yaml

from scripts.verify_image_contract import DISABLED_DAGS, EXPECTED_DAGS, REQUIRED_DAG_HELPERS


PROJECT_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = PROJECT_ROOT.parents[1]


def test_real_airflow_dag_import_contract():
    # Existing unit tests deliberately stub Airflow modules. Run the real
    # parser in a clean interpreter so those process-global stubs cannot make
    # this contract test pass or fail for the wrong reason.
    completed = subprocess.run(
        [sys.executable, str(PROJECT_ROOT / "scripts" / "verify_image_contract.py")],
        cwd=PROJECT_ROOT,
        check=True,
        capture_output=True,
        text=True,
    )
    result_line = next(
        line
        for line in completed.stdout.splitlines()
        if line.startswith("AIRFLOW_CONTRACT_JSON=")
    )
    result = json.loads(result_line.removeprefix("AIRFLOW_CONTRACT_JSON="))

    assert set(result["loaded_dags"]) == EXPECTED_DAGS
    assert set(result["disabled_dags"]) == DISABLED_DAGS
    assert set(result["dag_helpers"]) == REQUIRED_DAG_HELPERS
    assert result["airflow_version"] == "3.2.1"
    assert result["dependency_conflicts"] == []


def test_dockerfile_bundles_locked_runtime_and_source():
    dockerfile = (PROJECT_ROOT / "Dockerfile").read_text()

    assert "FROM apache/airflow:3.2.1-python3.12" in dockerfile
    assert "uv export" in dockerfile
    assert "--frozen" in dockerfile
    assert "--require-hashes" in dockerfile
    assert "COPY --chown=airflow:root dags/ /opt/airflow/dags/" in dockerfile
    assert "COPY --chown=airflow:root scripts/ /opt/airflow/scripts/" in dockerfile


def test_helm_values_use_custom_image_without_runtime_install_or_dag_pvc():
    values_path = REPO_ROOT / "k8s" / "airflow" / "values.yaml"
    values_text = values_path.read_text()
    values = yaml.safe_load(values_text)

    assert values["airflowVersion"] == "3.2.1"
    assert values["images"]["airflow"]["repository"].endswith("/airflow")
    assert values["registry"]["secretName"] == "ghcr-pull-secret"
    assert values["dags"]["persistence"]["enabled"] is False
    assert "_pip_additional_requirements" not in values
    assert "_PIP_ADDITIONAL_REQUIREMENTS" not in values_text

    global_env = {entry["name"]: entry["value"] for entry in values["env"]}
    assert global_env["AIRFLOW__DAG_PROCESSOR__MIN_FILE_PROCESS_INTERVAL"] == "120"
    assert global_env["AIRFLOW__SCHEDULER__SCHEDULER_HEALTH_CHECK_THRESHOLD"] == "180"
    assert "AIRFLOW__SCHEDULER__MIN_FILE_PROCESS_INTERVAL" not in global_env

    scheduler_env = {entry["name"]: entry for entry in values["scheduler"]["env"]}
    assert scheduler_env["OPENLINEAGE__TRANSPORT__TYPE"]["value"] == "datadog"
    assert scheduler_env["OPENLINEAGE_NAMESPACE"]["value"] == "dev"
    assert scheduler_env["DD_SITE"]["value"] == "us3.datadoghq.com"
    assert scheduler_env["DD_API_KEY"]["valueFrom"]["secretKeyRef"] == {
        "name": "airflow-azure-secret",
        "key": "DD_API_KEY",
    }
    assert "DD_DATA_JOBS_ENABLED" not in scheduler_env
    assert scheduler_env["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"]["value"] == (
        "text-embedding-3-small"
    )
    assert scheduler_env["SAMGOV_API_KEY"]["valueFrom"]["secretKeyRef"] == {
        "name": "airflow-azure-secret",
        "key": "SAMGOV_API_KEY",
    }
    assert scheduler_env["TWDB_WATER_PLAN_WORKBOOK_URL"]["value"] == (
        "https://www.twdb.texas.gov/waterplanning/data/rwp-database/doc/"
        "2027StateWaterPlanDataSummaryWorkbook_v1.zip"
    )

    for dag_path in (PROJECT_ROOT / "dags").glob("*.py"):
        assert "DD_DATA_JOBS_ENABLED" not in dag_path.read_text()


def test_airflow_secret_and_upgrade_fail_closed():
    makefile = (REPO_ROOT / "Makefile").read_text()
    workflow = (REPO_ROOT / ".github" / "workflows" / "build-push.yml").read_text()
    preflight = (PROJECT_ROOT / "scripts" / "cluster_preflight.sh").read_text()

    assert 'AccountName=placeholder' not in makefile
    assert '--from-literal=AZURE_STORAGE_CONNECTION_STRING="$(AZURE_STORAGE_CONNECTION_STRING)"' in makefile
    assert '--from-literal=SAMGOV_API_KEY="$(SAMGOV_API_KEY)"' in makefile
    assert "services/ingestion/scripts/cluster_preflight.sh" in makefile
    assert "services/ingestion/scripts/cluster_preflight.sh" in workflow
    assert "helm rollback airflow 0" not in workflow
    assert "deployment,statefulset,pod" in preflight
    assert "ghcr-pull-secret" in preflight
    assert 'STATUS' not in preflight


def test_airflow_delivery_is_verified_and_routine_install_is_non_destructive():
    makefile = (REPO_ROOT / "Makefile").read_text()
    workflow = (REPO_ROOT / ".github" / "workflows" / "build-push.yml").read_text()

    install_block = makefile.split("install-airflow:", 1)[1].split(
        "recover-airflow-destructive:", 1
    )[0]
    recovery_block = makefile.split("recover-airflow-destructive:", 1)[1].split(
        "preflight-airflow-cluster:", 1
    )[0]
    upgrade_header = next(
        line for line in makefile.splitlines() if line.startswith("upgrade-airflow:")
    )

    assert "verify-airflow-image" in install_block.splitlines()[0]
    assert "helm uninstall" not in install_block
    assert "kubectl delete namespace" not in install_block
    assert "AIRFLOW_DESTRUCTIVE_RECOVERY" in recovery_block
    assert "helm uninstall" in recovery_block
    assert "kubectl delete namespace" in recovery_block
    assert "create-airflow-ghcr-secret" in makefile
    assert "verify-airflow-image" in upgrade_header

    verify_step = workflow.index("Verify Airflow image contract before publishing")
    publish_step = workflow.index("Publish verified Airflow image")
    deploy_job = workflow.index("upgrade-airflow:")
    assert verify_step < publish_step < deploy_job
    assert "needs: [changes, build-airflow]" in workflow[deploy_job:]


def test_make_dry_runs_can_disable_local_dotenv_expansion():
    makefile = (REPO_ROOT / "Makefile").read_text()

    assert "ifneq ($(SKIP_DOTENV),1)" in makefile
    assert "-include .env" in makefile


def test_all_embedding_calls_use_the_configured_deployment_name():
    for dag_path in (PROJECT_ROOT / "dags").glob("*.py"):
        source = dag_path.read_text()
        assert "text-embedding-ada-002" not in source

        if ".embeddings.create(" in source:
            calls = re.findall(r"\.embeddings\.create\((.*?)\)", source, re.DOTALL)
            assert calls
            assert all("model=embedding_deployment" in call for call in calls)


def test_dags_use_the_airflow_3_python_operator_provider_path():
    for dag_path in (PROJECT_ROOT / "dags").glob("*.py"):
        source = dag_path.read_text()
        assert "from airflow.operators.python import PythonOperator" not in source
