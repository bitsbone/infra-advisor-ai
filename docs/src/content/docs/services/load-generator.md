---
title: Load generator
description: Understand what the synthetic Kafka loop proves—and what it does not
docType: concept
audience:
  - application-developer
  - observability-engineer
maturity: partial
verifiedOn: 2026-08-27
sidebar:
  order: 8
---

The load generator is a Kubernetes CronJob that samples a checked-in corpus and publishes queries to `infra.query.events`. A background consumer in the Python Agent API executes the ordinary agent path under a reserved system tenant and publishes a result envelope to `infra.eval.results`.

## Why it exists

- keep an observable producer/topic/consumer path for Data Streams Monitoring;
- exercise happy, edge, and adversarial questions without waiting for users;
- generate repeatable agent traces for investigation;
- reveal availability and latency regressions in the synthetic path.

It is not a complete regression-test framework. The current result event contains `faithfulness_score: null`; Python's asynchronous faithfulness task does not update the Kafka message later. A monitor on the separate gauge cannot prove a particular result event passed.

## Corpus design

Three YAML corpora represent expected use, boundary behavior, and adversarial inputs. Weighted sampling controls traffic mix, but random selection makes two runs non-identical. Corpus entries should use invented/non-sensitive text and stable IDs.

An `expected_answer_hash` in the event is derived from the query for matching/deduplication; it is not an expected model-answer oracle.

## Message contract

The query event carries query ID, synthetic session ID, query text, corpus type, expected domain, hash, and timestamp. The result carries answer, sources, tools, latency, corpus/domain metadata, and the currently empty faithfulness field.

Synthetic query content is intentionally sent through Kafka and the agent. Treat the corpus as public test data and do not insert production conversations or credentials.

## Operational behavior

The CronJob forbids overlapping runs. `ddtrace.auto` instruments Kafka production and the run adds a `load_generator.run` span with the query count. The consumer uses a system tenant prefix so synthetic state cannot collide with authenticated users.

To trigger a controlled run:

```bash
kubectl create job --from=cronjob/load-generator <unique-job-name> -n infra-advisor
```

Inspect the job, topic lag, consumer logs, output events, and agent traces. Remove the ad hoc Job only through the normal cluster cleanup policy.

## Path to real regression testing

A release gate needs a fixed dataset, versioned expected behavior, deterministic invocation metadata, joined evaluations, and candidate-versus-baseline comparison. Follow [Experiments](/infra-advisor-ai/llm-engineering/experiments/) rather than treating scheduled random traffic as that system.
