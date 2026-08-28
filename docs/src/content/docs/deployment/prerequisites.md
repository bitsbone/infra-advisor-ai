---
title: Prepare a deployment environment
description: Establish local tools, cloud access, registry access, Datadog prerequisites, and secret inputs before mutating Azure or AKS
docType: reference
audience:
  - platform-engineer
  - maintainer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 1
  label: Prerequisites
---

## Local tools

Install Azure CLI, `kubelogin`, `kubectl`, Helm, Docker, `uv`, Node.js, the .NET SDK used by the projects, and GNU Make-compatible tooling. Use project files, lockfiles, workflows, and Bicep API versions to select compatible releases; version numbers copied into prose age quickly.

Confirm each command resolves before beginning:

```bash
az version
kubectl version --client
helm version
docker version
uv --version
node --version
dotnet --info
```

## Access boundaries

You need:

- an Azure subscription and an identity authorized to deploy the Bicep resources;
- Azure OpenAI model access in the selected regions;
- a Datadog organization and API key for the Agent;
- a GHCR identity that can pull the repository's private images;
- authorized external-provider keys used by the current `check-env` contract;
- permission to install or manage the required Kubernetes operators.

CI authentication should use the repository's configured Azure identity workflow and least privilege. Do not create a new broad service-principal credential merely because an older guide used one.

## Datadog setup

Prepare the Datadog Operator/Agent prerequisites, target site, API key, browser RUM application, and any account-side Agent Observability or security configuration. Application keys are required only for workflows that call privileged read/write APIs, such as .NET AI Guard or administrative asset synchronization; do not distribute them to unrelated services.

## Environment contract

Copy the template and fill it from approved secret sources:

```bash
cp .env.example .env
set -a
source .env
set +a
make check-env
```

`make check-env` is authoritative for the baseline cluster deployment. It currently validates Azure OpenAI/Search/Storage, provider keys, Datadog, registry identity, PostgreSQL, authentication, Airflow admin, and Mailpit credentials.

Some feature inputs are optional at the individual secret-target level—for example Whisper or an application key—and produce an explicit warning when absent. Optional means the service degrades or disables that capability; it does not mean the feature remains fully operational.

## Secret handling

- Keep `.env` ignored and never attach it to issues, logs, or prompts.
- Create GHCR pull secrets independently in `infra-advisor` and `airflow`; Kubernetes secrets are namespace-scoped.
- Authenticate the local Docker client before Airflow image verification, which pulls the exact image before Helm changes the cluster.
- Store CI keys and signing material in the platform secret store.
- Rotate any value that appears in shell output, a committed file, or an exported artifact unexpectedly.

When `make check-env` passes, continue to the [Deployment quickstart](../quickstart/).
