FROM python:3.12-slim

LABEL org.opencontainers.image.title="load-generator" \
      org.opencontainers.image.description="Kubernetes CronJob that samples a checked-in query corpus and publishes synthetic load to InfraAdvisor's agent API for testing and evaluation." \
      org.opencontainers.image.source="https://github.com/bitsbone/infra-advisor-ai"

RUN pip install --no-cache-dir uv==0.4.18

WORKDIR /app

COPY pyproject.toml .
RUN uv pip install --system --no-cache -e .

COPY src/ ./src/

ENV PYTHONPATH=/app/src
ENV PYTHONUNBUFFERED=1

# Git metadata for Datadog source code integration
ARG DD_GIT_REPOSITORY_URL
ARG DD_GIT_COMMIT_SHA
ENV DD_GIT_REPOSITORY_URL=${DD_GIT_REPOSITORY_URL}
ENV DD_GIT_COMMIT_SHA=${DD_GIT_COMMIT_SHA}

CMD ["python", "src/main.py"]
