---
title: Maintainer Invariants
description: Cross-cutting rules that prevent security, routing, schema, and observability regressions
docType: maintainer
audience:
  - contributor
maturity: stable
verifiedOn: 2026-08-27
---

Most style choices are enforced by each language's formatter, linter, project file, and tests. This page records the smaller set of project invariants that are easy to violate across service boundaries.

For routine language and repository conventions, see [Development conventions](/infra-advisor-ai/development/conventions/).

## Protect configuration and user data

- Required server-side configuration must fail clearly when absent. Do not hide a missing credential behind a production-like default.
- Keep privileged keys, connection strings, JWT secrets, private endpoints, and real user data out of source control and documentation.
- Browser and mobile client identifiers may be public only when their provider explicitly designs them for distribution. They are not substitutes for privileged API or application keys.
- Treat prompts, responses, attachments, and user identifiers as potentially sensitive telemetry. Capture the minimum needed for the experiment and document the boundary.
- Create Kubernetes Secrets from ignored local configuration or a secret manager. Checked-in manifests should contain references or obvious placeholders only.

## Preserve public ingress behavior

The UI nginx configuration is the cluster's public routing layer. Cluster services remain internal unless nginx exposes a route for them.

When adding or changing a public subpath, verify all of these together:

1. `services/ui/nginx.conf` routes the prefix to the correct Kubernetes Service.
2. The upstream either understands its base path or nginx deliberately strips the prefix.
3. Streaming routes disable buffering and allow sufficient timeouts.
4. The UI image is rebuilt because nginx configuration is copied into that image.
5. Administrative surfaces have the intended access control before reaching nginx.

Test the public route as well as the service directly. A localhost or ClusterIP response cannot reveal prefix rewriting, buffering, or external authentication failures.

## Keep service contracts explicit

- Python and .NET implementations are learning alternatives, not assumed replicas. Preserve intentional differences and document parity only where behavior is meant to match.
- Update callers, tests, and docs together when an HTTP, streaming-event, MCP-tool, or persistence contract changes.
- Use the live MCP schemas as the complete tool contract. The documentation's [tool catalog](/infra-advisor-ai/services/mcp-tools/) explains selection and domain boundaries rather than duplicating every field.
- Preserve source dataset field names at ingestion boundaries. Map them into internal models only in an explicit transformation layer.
- Kafka messages should carry stable, versionable payloads. The current evaluation-result stream is operational telemetry, not a substitute for the evaluation systems described under [Evaluations](/infra-advisor-ai/llm-engineering/evaluations/managed/).

## Make telemetry answer a question

Instrumentation should establish a useful relationship, such as request → agent run → model call → tool call, rather than maximize event volume.

- Keep `service`, `env`, and `version` consistent enough to correlate signals.
- Use bounded tags for metrics; do not turn prompts, IDs, or arbitrary errors into metric tags.
- Mark errors on the span that owns the failing operation and retain enough context to identify the boundary.
- Do not claim browser RUM, APM, and Agent Observability sessions are correlated unless the implementation propagates and records the relevant identifiers.
- Verify instrumentation in the Datadog product surface it is meant to populate.

The Python and .NET paths intentionally differ. See [Instrumentation paths](/infra-advisor-ai/llm-engineering/instrumentation/paths/) before copying an approach between runtimes.

## Respect deployment ownership

- Application workloads use the `infra-advisor` namespace; Kafka and Datadog have dedicated namespaces.
- Application deployments that pull private images require the repository's GHCR pull secret.
- `make deploy-k8s` and the Datadog Operator workflow have separate ownership. Do not assume one installs the other.
- Azure owns the resources inside the AKS node resource group. Change their desired behavior through AKS, Bicep, or Kubernetes—not by editing node resources directly.
- Check the current Makefile and manifests before using commands copied from historical notes.

## Document the learning, not the patch

A feature change should improve the public learning experience when it changes a durable behavior, concept, experiment, or verification path. It does not automatically justify a new page or a fixed page template.

Use [Documentation approach](/infra-advisor-ai/agent-guides/documentation/) to select an appropriate content shape and to distinguish learner material from maintainer history.
