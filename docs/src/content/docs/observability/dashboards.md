---
title: Validate dashboards and monitors
description: Understand the checked-in Datadog assets, their evidence sources, and the difference between repository intent and deployed state
docType: guide
audience:
  - observability-engineer
  - platform-engineer
maturity: partial
verifiedOn: 2026-08-27
sidebar:
  order: 4
  label: Dashboards & monitors
---

The `datadog` directory stores JSON definitions for observability assets. A checked-in definition documents intent and supports repeatable import; it does not prove the asset is deployed, receiving data, or owned by an on-call workflow.

## Asset map

| Asset | Question it should answer | Primary evidence |
|---|---|---|
| Infrastructure Overview dashboard | Are cluster and shared data services healthy? | Kubernetes, Kafka, Redis, and host metrics |
| LLM Observability dashboard | How are agent volume, latency, tools, feedback, and quality trending? | Agent spans, evaluations, and custom metrics |
| MCP Server dashboard | Which tools or providers are slow or failing? | MCP spans, logs, and metrics |
| Pipeline Health dashboard | Did scheduled ingestion complete and publish data? | Airflow/Data Jobs and custom task spans |
| Blob Storage dashboard | Are source snapshots uploading successfully? | `azure.blob.upload` spans |
| Faithfulness monitor | Has the Python faithfulness gauge degraded? | `eval.faithfulness_score` |
| Kafka lag monitor | Is the synthetic consumer falling behind? | consumer lag |
| MCP provider-error monitor | Are provider failures elevated? | MCP error metric |
| Consultant Query synthetic | Can a user complete the core browser journey? | browser test |

## Validate an asset before trusting it

1. Open the JSON and identify every metric, span, tag, service, and environment it expects.
2. Find a recent source event and confirm its exact field names and units.
3. Import or update the asset through an authorized Datadog workflow.
4. Exercise a known healthy and known failing case.
5. Confirm the dashboard distinguishes Python and .NET where their signal contracts differ.
6. Assign an owner and record the deployed asset ID or URL outside secrets.

The LLM dashboard deserves special care: Python and .NET do not emit identical business metrics or evaluation types. A combined widget must filter or group by backend rather than imply parity.

## Monitor design

A monitor should state what decision follows the alert. Validate its query over representative history before accepting a threshold. Confirm no-data behavior, evaluation delay, recovery window, notification route, and a safe test method.

For faithfulness specifically, the current Python score is a gauge emitted by an asynchronous judge. Missing points can mean no sampled/evaluable work, judge failure, DogStatsD failure, or no traffic. Treat absence separately from a low score.

## Import boundary

Dashboard and monitor APIs require an application key with write access. Keep that key in a deployment or administrative workload, not an agent pod or browser. Prefer a reviewed synchronization workflow over copying credentials into local shell history.

After import, compare the deployed JSON with the repository definition. Datadog UI edits can create drift even when the checked-in file still builds successfully.

Continue to [Metrics](/infra-advisor-ai/llm-engineering/monitoring/metrics/) for signal selection and [Operations](/infra-advisor-ai/llm-engineering/monitoring/operations/) for alert maturity.
