FROM python:3.12-slim

# Install uv
COPY --from=ghcr.io/astral-sh/uv:latest /uv /uvx /usr/local/bin/

WORKDIR /app

# Copy dependency files first for layer caching
COPY pyproject.toml uv.lock ./

# Install production dependencies from the committed lockfile — --frozen
# makes the build fail rather than silently re-resolve if uv.lock and
# pyproject.toml ever drift out of sync. Without this (the previous
# `uv pip install --system -e .` invoked no lockfile at all), every image
# build re-resolved transitive dependencies fresh against whatever was
# newest on PyPI that day — which is how a `mcp` release that removed
# `mcp.shared.session` (a module ddtrace's mcp integration patch imports)
# broke this service without any dependency version change in this repo.
# Matches the pattern already used by services/mcp-server/Dockerfile.
RUN uv sync --no-dev --frozen

COPY src/ ./src/

ENV PYTHONPATH=/app/src
ENV PYTHONUNBUFFERED=1

# Git metadata for Datadog source code integration
ARG DD_GIT_REPOSITORY_URL
ARG DD_GIT_COMMIT_SHA
ENV DD_GIT_REPOSITORY_URL=${DD_GIT_REPOSITORY_URL}
ENV DD_GIT_COMMIT_SHA=${DD_GIT_COMMIT_SHA}

RUN groupadd --gid 1000 appuser && useradd --uid 1000 --gid 1000 --no-create-home --shell /bin/false appuser
USER appuser

EXPOSE 8001

CMD ["/app/.venv/bin/uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8001"]
