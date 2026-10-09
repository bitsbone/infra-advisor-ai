---
title: Navigate Kubernetes resources
description: Map namespaces and workload ownership, then inspect live manifests for volatile replica, image, port, and resource values
docType: reference
audience:
  - platform-engineer
  - maintainer
maturity: stable
verifiedOn: 2026-10-09
sidebar:
  order: 3
  label: Kubernetes resources
---

## Namespace ownership

| Namespace | Owner and contents |
|---|---|
| `infra-advisor` | Product APIs, both MCP servers, UI, Redis, PostgreSQL, Mailpit, and synthetic load |
| `kafka` | Strimzi operator resources, Kafka cluster, and topics |
| `datadog` | Datadog Agent and Cluster Agent managed through the Operator |

Ingestion (Azure Data Factory + Azure Functions) is not a Kubernetes workload — see the [data pipeline overview](/data-pipeline/) for that architecture instead.

Replica counts, images, ports, probes, resource requests, and schedules change frequently. Inspect `k8s/<workload>` and the live object rather than copying a table from documentation.

## How manifests are composed

The manifests use [Kustomize](https://kubectl.docs.kubernetes.io/references/kustomize/), which is built into `kubectl`. Each `k8s/<workload>/` directory is a base with its own `kustomization.yaml`, and `k8s/overlays/<env>/` selects the bases for one environment. `make deploy-k8s` applies `k8s/overlays/$(ENV)`, with `ENV` defaulting to `prod`.

Apply a directory with `-k`, not `-f`: `kubectl apply -f k8s/agent-api/` now fails on the `kustomization.yaml` file because it isn't a Kubernetes object. CI applies a single changed service the same way, with `kubectl apply -k k8s/<service>`.

Preview a change before you apply it:

```bash
kubectl kustomize k8s/overlays/prod      # render locally
kubectl diff -k k8s/overlays/prod        # compare with the live cluster
uv run --no-project --with pytest --with pyyaml pytest -q k8s/tests   # overlay contract tests (also run in CI)
```

A non-empty `kubectl diff` isn't necessarily a regression. CI pins each Deployment to a commit-SHA image, while the checked-in manifests say `:latest`, so image lines always differ.

## Application namespace

The public UI service routes to `auth-api`, `agent-api`, and `agent-api-dotnet`. Each agent calls its language-matched MCP service. Redis supplies ephemeral memory, PostgreSQL supplies durable identity/conversation state, and the load-generator CronJob publishes synthetic queries to Kafka.

Secrets are split by responsibility: registry pull, each service's external credentials, PostgreSQL, Redis, Datadog DBM, and Mailpit. All GHCR workloads reference the namespace-local `ghcr-pull-secret`.

## Kafka namespace

Strimzi manages the cluster and two project topics:

- `infra.query.events` carries synthetic requests from the load generator;
- `infra.eval.results` carries the Python consumer's result envelope.

The result topic is not a durable evaluation database, and its current faithfulness field remains unpopulated.

## Datadog namespace

The `DatadogAgent` custom resource configures cluster, APM/OTLP, logs, security, Data Streams, process, network, and related capabilities. Product enablement still depends on Agent/Operator compatibility, credentials, and account configuration; a YAML field alone is not runtime proof.

## Safe inspection commands

```bash
make check-pods
make logs-agent
make logs-mcp

kubectl get deploy,statefulset,daemonset,cronjob -A
kubectl describe pod <pod> -n <namespace>
kubectl get events -n <namespace> --sort-by=.lastTimestamp
kubectl rollout status deployment/<name> -n <namespace>
kubectl get configmap <name> -n <namespace> -o yaml
```

Avoid `kubectl exec ... -- bash` as a default assumption: minimal production images may not contain Bash or debugging tools. Prefer logs, `describe`, ephemeral debug containers, or a purpose-built diagnostic image.

Before scaling or restarting a workload, identify whether it owns ephemeral state, uses a persistent volume, participates in a leader election, or runs in-flight background evaluation work.
