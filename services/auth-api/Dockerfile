FROM python:3.12-slim

LABEL org.opencontainers.image.title="auth-api" \
      org.opencontainers.image.description="Auth API for InfraAdvisor AI — owns user identity and bearer token issuance/validation." \
      org.opencontainers.image.source="https://github.com/bitsbone/infra-advisor-ai"

WORKDIR /app

RUN pip install uv

COPY pyproject.toml .
RUN uv pip install --system --no-cache .

COPY src/ ./src/

ENV PYTHONPATH=/app/src

RUN groupadd --gid 1000 appuser && useradd --uid 1000 --gid 1000 --no-create-home --shell /bin/false appuser
USER appuser

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8002"]
