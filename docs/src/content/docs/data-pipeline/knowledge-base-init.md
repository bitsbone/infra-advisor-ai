---
title: Initialize the synthetic knowledge corpus
description: Seed the learning environment with clearly labeled fictional firm documents
docType: guide
audience:
  - data-engineer
  - maintainer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  label: Knowledge base init
---

**DAG:** `knowledge_base_init` · **Schedule:** manual only

This DAG runs one image-bundled generation script that creates a synthetic firm knowledge corpus and indexes it into Azure AI Search. It exists to demonstrate retrieval over internal-style knowledge without publishing real consulting documents.

## What it creates

The generator produces fictional examples such as proposals, lessons learned, cost benchmarks, risk frameworks, and funding guides. Every downstream explanation should identify this material as synthetic; it is not evidence of actual project experience, costs, or policy.

The script is idempotent at its current threshold: when the expected synthetic corpus already exists, it can skip paid generation work. Re-running is a corpus refresh decision, not a routine scheduled ingestion.

## Trigger and verify

```bash
kubectl exec -n airflow airflow-scheduler-0 -c scheduler -- \
  airflow dags trigger knowledge_base_init
```

`make run-dags` also triggers this DAG alongside selected source canaries. Use the direct command when only synthetic initialization is intended.

Verify that:

- the immutable Airflow image contains `generate_synthetic_docs` and its dependencies;
- generated documents carry the synthetic source/domain label;
- the Search index contains the intended corpus without duplicate expansion;
- `search_project_knowledge` can retrieve a known synthetic example;
- prompts and generated document bodies are not copied into operational logs.

If the Search index or schema is missing, fix infrastructure/index initialization before retrying generation. Do not turn a non-retriable schema error into an uncontrolled model loop.
