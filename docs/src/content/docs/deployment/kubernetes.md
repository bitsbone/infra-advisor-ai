---
title: Navigate Kubernetes resources
description: Map namespaces and workload ownership, then inspect live manifests for volatile replica, image, port, and resource values
docType: reference
audience:
  - platform-engineer
  - maintainer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 3
  label: Kubernetes resources
---

## Namespace ownership

| Namespace | Owner and contents |
|---|---|
| `infra-advisor` | Product APIs, both MCP servers, UI, Redis, PostgreSQL, Mailpit, and synthetic load |
| `airflow` | Airflow scheduler, API, DAG processor, triggerer, metadata database, and logs |
| `kafka` | Strimzi operator resources, Kafka cluster, and topics |
| `datadog` | Datadog Agent and Cluster Agent managed through the Operator |

Replica counts, images, ports, probes, resource requests, and schedules change frequently. Inspect `k8s/<workload>` and the live object rather than copying a table from documentation.

## Application namespace

The public UI service routes to `auth-api`, `agent-api`, and `agent-api-dotnet`. Each agent calls its language-matched MCP service. Redis supplies ephemeral memory, PostgreSQL supplies durable identity/conversation state, and the load-generator CronJob publishes synthetic queries to Kafka.

Secrets are split by responsibility: registry pull, each service's external credentials, PostgreSQL, Redis, Datadog DBM, Airflow, and Mailpit. All GHCR workloads reference the namespace-local `ghcr-pull-secret`.

## Airflow namespace

The Helm release uses an immutable application image containing DAGs and helpers. DAG persistence and git-sync are disabled. Airflow's registry secret must exist in its own namespace and apply to scheduler, processor, API, triggerer, migration, and hook workloads.

Use `make preflight-airflow-cluster` before upgrades and the image verification target before Helm mutation.

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
