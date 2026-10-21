FROM python:3.12-slim

LABEL org.opencontainers.image.title="auth-api" \
      org.opencontainers.image.description="Auth API for InfraAdvisor AI — owns user identity and bearer token issuance/validation." \
      org.opencontainers.image.source="https://github.com/bitsbone/infra-advisor-ai"

WORKDIR /app

COPY --from=ghcr.io/astral-sh/uv:latest /uv /uvx /usr/local/bin/

# Install from the committed lockfile, like agent-api and mcp-server. The old
# `uv pip install --system .` ignored uv.lock, so each build re-resolved: when
# SQLAlchemy 2.1 made psycopg 3 the default postgresql:// driver, a fresh image
# crashed on import with "No module named 'psycopg'".
COPY pyproject.toml uv.lock ./
RUN uv sync --no-dev --frozen

COPY src/ ./src/

ENV PYTHONPATH=/app/src

RUN groupadd --gid 1000 appuser && useradd --uid 1000 --gid 1000 --no-create-home --shell /bin/false appuser
USER appuser

CMD ["/app/.venv/bin/uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8002"]
