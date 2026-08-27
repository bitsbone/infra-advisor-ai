# syntax=docker/dockerfile:1.7

# Airflow and Python are pinned to the versions resolved in uv.lock. Updating
# either version requires updating this line, pyproject.toml, and uv.lock in the
# same change so the scheduler, migration job, and DAG parser cannot drift.
FROM apache/airflow:3.2.1-python3.12

ARG UV_VERSION=0.11.7

USER airflow

COPY --chown=airflow:root pyproject.toml uv.lock /tmp/ingestion/

# Export the checked-in lock and install its exact, hashed artifacts. Runtime
# pods never resolve or install packages; all dependencies are sealed here.
RUN pip install --no-cache-dir "uv==${UV_VERSION}" \
    && uv export \
        --project /tmp/ingestion \
        --frozen \
        --no-dev \
        --no-emit-project \
        --output-file /tmp/ingestion-requirements.txt \
    && pip install --no-cache-dir --require-hashes -r /tmp/ingestion-requirements.txt \
    && rm -rf /tmp/ingestion /tmp/ingestion-requirements.txt

# Bundle DAGs and their helper scripts into one immutable image. This avoids the
# scheduler/dag-processor skew caused by copying only dags/ into a shared PVC.
COPY --chown=airflow:root dags/ /opt/airflow/dags/
COPY --chown=airflow:root scripts/ /opt/airflow/scripts/
