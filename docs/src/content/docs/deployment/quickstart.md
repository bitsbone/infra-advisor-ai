---
title: Quickstart
description: Step-by-step deployment from zero to running application
---

Complete deployment from a clean Azure subscription to a running application.

## 1. Clone and configure

```bash
git clone https://github.com/kyletaylored/infra-advisor-ai.git
cd infra-advisor-ai
cp .env.example .env
# Edit .env with your values (see Prerequisites)
set -a && source .env && set +a
```

## 2. Deploy Azure infrastructure

```bash
az login
az account set --subscription <your-subscription-id>
make deploy-infra
```

This provisions:
- AKS cluster (3× Standard_D2s_v3)
- Azure OpenAI (4 model deployments)
- Azure AI Search (Standard tier)
- Azure Blob Storage
- Log Analytics workspace

**Duration:** 10–15 minutes

## 3. Get AKS credentials

```bash
make get-credentials
kubectl get nodes   # verify 3 nodes Ready
```

## 4. Create GHCR pull secret

```bash
make create-ghcr-secret
```

## 5. Create all secrets

```bash
make create-secrets
```

This runs all individual secret targets:
- `create-mcp-server-secret`
- `create-agent-api-secret` — Azure OpenAI keys + optional `DATABASE_URL`
- `create-agent-api-dotnet-secret` — same keys for the .NET backend
- `create-auth-api-secret`
- `create-postgres-secret`
- `create-dd-postgres-secret`
- `create-airflow-secret`
- `create-load-generator-secret`

**Enabling conversation persistence (optional):** Set `DATABASE_URL` in your `.env` before running `make create-secrets`. Both Agent API services read this variable; if unset, conversation history is silently disabled and the sidebar shows no past conversations.

```bash
# .env
DATABASE_URL=postgresql://appuser:password@postgres.infra-advisor.svc.cluster.local:5432/infraadvisor
```

## 6. Deploy Kubernetes workloads

```bash
make deploy-k8s
```

This applies in order:
1. Namespaces
2. Strimzi Operator CRDs (with `kubectl wait --for=condition=established`)
3. Kafka cluster and topics
4. Redis
5. PostgreSQL
6. Datadog Agent (DatadogAgent CR)
7. Mailpit (bcrypt basic auth)
8. MCP Server
9. Agent API
10. Auth API
11. Load Generator
12. UI
13. Airflow (Helm install)

**Duration:** 5–10 minutes for all pods to reach Running state.

## 7. Verify pods

```bash
kubectl get pods -n infra-advisor
kubectl get pods -n airflow
kubectl get pods -n kafka
kubectl get pods -n datadog
```

All pods should show `Running` status. Airflow dependencies, DAGs, and helper scripts are already installed in its verified custom image; pods never install packages at startup.

## 8. Initialize the knowledge base

```bash
make run-dags       # trigger the approved canary DAGs bundled in the Airflow image
```

The `knowledge_base_init` DAG must complete before `search_project_knowledge` returns results. Monitor progress in the Airflow UI:
```
https://infra-advisor-ai.kyletaylor.dev/airflow
```

## 9. Get the application URL

```bash
kubectl get svc -n infra-advisor ui -o jsonpath='{.status.loadBalancer.ingress[0].ip}'
```

Point your DNS record (or use the IP directly):
```
https://infra-advisor-ai.kyletaylor.dev
```

## 10. Register a user

Navigate to the application URL, click **Register**, and create your account. The first user becomes an admin automatically.

---

## Upgrade deployments

After pushing code changes (handled automatically by CI on merge to `main`):

```bash
# Manually force a rollout if needed:
kubectl rollout restart deployment/agent-api -n infra-advisor
kubectl rollout restart deployment/mcp-server -n infra-advisor
kubectl rollout restart deployment/auth-api -n infra-advisor
kubectl rollout restart deployment/ui -n infra-advisor
```

## Upgrade Airflow config

After changing `k8s/airflow/values.yaml`:

```bash
make create-airflow-ghcr-secret
make upgrade-airflow AIRFLOW_IMAGE_TAG=<git-commit-sha>
```

The upgrade is intentionally fail-closed: it requires a deployed, single-image release with current metadata migrations and valid application/registry secrets, pulls the requested image, runs its real DagBag/runtime contract locally, performs an atomic Helm upgrade, and verifies the live workloads against the immutable image afterward. Resolve a failed preflight instead of uninstalling the release or deleting the namespace.

## Deliver DAG changes

After modifying DAG files in `services/ingestion/dags/`:

```bash
make test-airflow
make test-airflow-container
# Build and publish an immutable image, then run the verified upgrade above.
```

DAG changes are never copied into a live pod or PVC. The DAG processor scans the image-bundled directory at the configured two-minute interval, and the scheduler health threshold is three minutes so the one-minute Kubernetes health probe remains tolerant of the intentionally quieter scan cadence.
