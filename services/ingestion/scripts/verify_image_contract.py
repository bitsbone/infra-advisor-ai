"""Validate the ingestion image without contacting external services.

This check intentionally uses Airflow's real ``DagBag`` parser. It catches
missing runtime packages, missing helper scripts, and DAG import failures that
unit tests with stubbed Airflow modules cannot detect.
"""

from __future__ import annotations

import json
import os
import tempfile
from importlib.metadata import PackageNotFoundError, distributions, version
from pathlib import Path

from packaging.requirements import InvalidRequirement, Requirement


EXPECTED_DAGS = {
    "census_market_intelligence_refresh",
    "eia_refresh",
    "fema_refresh",
    "knowledge_base_init",
    "nbi_refresh",
    "public_docs_ingestion",
    "samgov_awards_refresh",
    "twdb_water_plan_refresh",
}
DISABLED_DAGS = {"spark_feature_engineering"}
REQUIRED_SCRIPTS = {
    "fetch_public_docs.py",
    "generate_synthetic_docs.py",
}
REQUIRED_DAG_HELPERS = {
    "_blob_manifest.py",
    "_dd_blob.py",
}


def installed_dependency_conflicts() -> list[str]:
    """Return installed requirements whose selected versions are incompatible."""
    conflicts: list[str] = []
    for distribution in distributions():
        owner = distribution.metadata.get("Name", "unknown")
        for raw_requirement in distribution.requires or []:
            try:
                requirement = Requirement(raw_requirement)
            except InvalidRequirement:
                conflicts.append(f"{owner}: invalid requirement {raw_requirement!r}")
                continue
            if requirement.marker is not None and not requirement.marker.evaluate({"extra": ""}):
                continue
            try:
                installed = version(requirement.name)
            except PackageNotFoundError:
                conflicts.append(f"{owner}: missing {requirement.name}")
                continue
            if requirement.specifier and not requirement.specifier.contains(
                installed, prereleases=True
            ):
                conflicts.append(
                    f"{owner}: {requirement.name} {installed} does not satisfy {requirement.specifier}"
                )
    return sorted(set(conflicts))


def verify(root: Path | None = None) -> dict[str, object]:
    """Return a machine-readable contract summary or raise ``RuntimeError``."""
    project_root = root or Path(__file__).resolve().parents[1]
    dags_dir = project_root / "dags"
    scripts_dir = project_root / "scripts"

    missing_scripts = sorted(
        name for name in REQUIRED_SCRIPTS if not (scripts_dir / name).is_file()
    )
    if missing_scripts:
        raise RuntimeError(f"Missing Airflow helper scripts: {missing_scripts}")
    missing_dag_helpers = sorted(
        name for name in REQUIRED_DAG_HELPERS if not (dags_dir / name).is_file()
    )
    if missing_dag_helpers:
        raise RuntimeError(f"Missing Airflow DAG helpers: {missing_dag_helpers}")

    dependency_conflicts = installed_dependency_conflicts()
    if dependency_conflicts:
        raise RuntimeError(
            "Installed package requirements are inconsistent: "
            + "; ".join(dependency_conflicts)
        )

    # Airflow initializes logging and a local SQLite metadata path on import.
    # Keep every generated file in a disposable directory so this check is safe
    # on developer machines and CI runners.
    with tempfile.TemporaryDirectory(prefix="infra-advisor-airflow-") as airflow_home:
        os.environ["AIRFLOW_HOME"] = airflow_home
        os.environ["AIRFLOW__CORE__LOAD_EXAMPLES"] = "false"
        os.environ["DD_TRACE_ENABLED"] = "false"

        import pyarrow  # noqa: F401 - import is the Parquet runtime contract
        from airflow.models import DagBag

        dag_bag = DagBag(
            dag_folder=str(dags_dir),
            include_examples=False,
            safe_mode=False,
        )

    if dag_bag.import_errors:
        errors = {str(path): error for path, error in dag_bag.import_errors.items()}
        raise RuntimeError(f"Airflow DAG import errors: {json.dumps(errors, sort_keys=True)}")

    loaded_dags = set(dag_bag.dag_ids)
    missing_dags = sorted(EXPECTED_DAGS - loaded_dags)
    unexpected_disabled = sorted(DISABLED_DAGS & loaded_dags)
    if missing_dags or unexpected_disabled:
        raise RuntimeError(
            "Airflow DAG contract mismatch: "
            f"missing={missing_dags}, disabled_but_loaded={unexpected_disabled}"
        )

    return {
        "airflow_version": version("apache-airflow"),
        "pyarrow_version": version("pyarrow"),
        "loaded_dags": sorted(loaded_dags),
        "disabled_dags": sorted(DISABLED_DAGS),
        "dag_helpers": sorted(REQUIRED_DAG_HELPERS),
        "helper_scripts": sorted(REQUIRED_SCRIPTS),
        "dependency_conflicts": dependency_conflicts,
    }


if __name__ == "__main__":
    # Airflow writes structured startup logs to stdout. Prefix the single-line
    # result so callers can extract it without depending on logging settings.
    print(f"AIRFLOW_CONTRACT_JSON={json.dumps(verify(), sort_keys=True)}")
