---
title: Prod and dev environments
description: Tell prod and dev apart in Datadog, deploy a pull request to dev, and know what each environment runs, shares, and isolates
docType: reference
audience:
  - platform-engineer
  - maintainer
maturity: experimental
verifiedOn: 2026-10-09
sidebar:
  order: 4
  label: Environments
---

InfraAdvisor runs two environments on the same AKS cluster. Each is a Kubernetes namespace with its own Datadog `env` tag, so every trace, log, metric, RUM session, and LLM Observability span can be filtered to one environment.

| | Prod | Dev |
|---|---|---|
| Namespace | `infra-advisor` | `infra-advisor-dev` |
| URL | `https://infra-advisor-ai.bitsbone.com` | `https://infra-advisor-ai-dev.bitsbone.com` |
| Datadog `env` | `prod` | `dev` |
| Kustomize overlay | `k8s/overlays/prod` | `k8s/overlays/dev` |
| Deployed by | merge to `main` (`build-push.yml`, after CI passes) | the `deploy:dev` PR label (`deploy-dev.yml`) |
| Images | `<sha>` and `latest` | `<sha>` and `pr-<n>`, never `latest` |
| UI banner | none | `DEV environment · <sha> · test data only` |

Dev is one shared namespace, not one per PR. The last PR deployed wins, and its PR comment says so.

## What dev runs and shares

Dev runs only the core app and APIs: `ui`, `auth-api`, `agent-api`, `agent-api-dotnet`, `mcp-server`, and `mcp-server-dotnet`, each at one replica with no autoscaling and trimmed CPU requests.

| | In dev |
|---|---|
| **Separate** | PostgreSQL (own volume), Redis, Mailpit, `JWT_SECRET`, admin and test users, chat media (`chat-media-dev` Blob container) |
| **Shared, read-only** | Azure AI Search index (dev holds a query key, not the admin key), the public data APIs behind the MCP tools, the Datadog Prompt Registry |
| **Shared quota** | Azure OpenAI and Whisper deployments |
| **Not deployed** | Kafka topics and consumers (`KAFKA_ENABLED=false`), the load generator, CronJobs, ADF ingestion |

Because the databases and `JWT_SECRET` are separate, a login token from one environment is rejected by the other.

## How isolation actually works

The cluster has no NetworkPolicy engine (`networkPolicy: none`), so namespaces aren't network-isolated: a dev pod could reach a prod Service by its full DNS name. Isolation comes from configuration instead:

- Workloads address in-namespace dependencies by short Service name (`redis`, `mcp-server`, `mailpit`), which resolves inside the pod's own namespace. Only nginx uses full names, and it builds them from the pod's namespace at start.
- Dev's secrets hold dev credentials only, so dev can't authenticate to prod's datastores.
- `KAFKA_ENABLED=false` keeps both agents out of prod's Kafka consumer group. Kafka is shared and plaintext, so a dev consumer would otherwise take prod's partitions.
- The `k8s/tests` contract tests fail if the dev overlay renders anything outside `infra-advisor-dev`, any `infra-advisor.svc` reference, Kafka or CronJob resources, or a non-`dev` env tag.

## Where the env tag comes from

- **Kubernetes workloads.** Each service's ConfigMap sets `DD_ENV`, and its Deployment carries the `tags.datadoghq.com/env` label (the dev overlay sets both to `dev`). The Datadog Agent sets no cluster-wide `env` tag, because both namespaces share the cluster.
- **UI and RUM.** The UI image is environment-neutral. The pod's own `tags.datadoghq.com/env` label becomes `APP_ENV` (Downward API), and nginx serves it as `/env-config.js`. RUM reads `env` from there, and so does the banner. See [RUM](/infra-advisor-ai/observability/rum/).
- **.NET OpenTelemetry services.** `TelemetrySetup.cs` copies `DD_ENV` and `DD_VERSION` onto the OTel resource as `deployment.environment` and `service.version`.
- **Version.** Every deploy stamps the image's short commit SHA into `DD_VERSION` and the `tags.datadoghq.com/version` labels (`.github/scripts/pin-deployment.sh`), so `version:<sha>` identifies exactly what ran in either environment.
- **PostgreSQL DBM.** The check annotation carries `env:` and a per-environment `reported_hostname` (`infra-advisor-postgres` / `infra-advisor-postgres-dev`).
- **ADF ingestion Functions.** They feed the prod search index, so they report `env:prod` through the `ddEnv` Bicep parameter, even though the Azure deployment's `environment` parameter (used in resource names) is `dev`.
- **Mobile apps.** Release builds report `env:prod`; Debug and local builds report `env:dev`.

## Deploy a pull request to dev

1. Add the `deploy:dev` label to the PR. While the label stays on, each new push redeploys. To redeploy without a push, run **Deploy PR to dev** from the Actions tab with the PR number.
2. The workflow builds the six core images at the PR's head commit, applies `k8s/overlays/dev`, pins every Deployment to that SHA, and waits for the rollouts. A rollout that doesn't finish is rolled back to the image dev ran before, and the run fails.
3. It reruns the seed Job, runs the consultant-flow Synthetics test against dev (once configured), and comments on the PR with links to the app and to APM, RUM, and LLM Observability filtered to `env:dev version:<sha>`.

Fork PRs can't deploy: GitHub gives them no secrets or OIDC token. The workflow doesn't wait for CI, so check CI before you trust a dev result.

The deploy job runs in the GitHub environment `dev`. Its `AZURE_CLIENT_ID` belongs to an identity that can write only to `infra-advisor-dev`, so code from a pull request can't change prod.

## Seeded data and the synthetic user

Dev never receives prod data. `make seed-dev` (also run by every PR deploy) is idempotent and creates:

- the **synthetic-test service account** (`SYNTHETIC_USER_EMAIL`), whose password is reset to `SYNTHETIC_USER_PASSWORD` on every run;
- one **demo service account per job role** (`demo-<role>@infra-advisor.test`, same password), for testing per-user prompt targeting;
- two sample conversations per demo user, so conversation history isn't empty.

The same synthetic account exists in prod, with the same credentials: `ADMIN_EMAIL=<your admin account> make create-synthetic-user ENV=prod` prompts for that account's password, then creates or updates the synthetic account and touches nothing else. Without `ADMIN_EMAIL` it signs in as `BOOTSTRAP_ADMIN_*`, which only works if that bootstrap account exists in the target database. One Synthetics test (`datadog/synthetics/consultant-query-flow.json`) signs in with it and serves both environments. Its start URL is the `BASE_URL` global variable (prod) for scheduled runs, and the dev deploy overrides it with `--override startUrl=… --override variables.BASE_URL=…`. Filter the account out of usage analysis by its email.

## First-time dev setup

These steps need credentials or rights the CI identity doesn't have, so a maintainer runs them once:

1. **Dev overrides.** Create the git-ignored `.env.dev`. It's loaded after `.env` for `ENV=dev`, and `make deploy-k8s ENV=dev` refuses to run without it. Set dev values for `JWT_SECRET`, `POSTGRES_PASSWORD`, `DATABASE_URL` (pointing at `postgres:5432` with the dev password), `REDIS_PASSWORD`, `MAILPIT_UI_PASSWORD`, `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, `AZURE_SEARCH_API_KEY` (a query key: `az search query-key create`), `SYNTHETIC_USER_EMAIL`, and `SYNTHETIC_USER_PASSWORD`.
2. **Azure.** Run `make deploy-infra` to create the `chat-media-dev` container.
3. **Cluster.** Run `make deploy-k8s ENV=dev`, then `make setup-postgres-dbm ENV=dev` with the same `DD_POSTGRES_PASSWORD` as prod (the Datadog Agent has one), then `make seed-dev`.
4. **Datadog Agent.** Run `make apply-datadog-agent` so dev's `agent-api-dotnet` gets the same App and API Protection injection as prod.
5. **CI identity.** Run `make setup-dev-deploy-identity` to create the namespace-scoped deploy identity and the GitHub `dev` environment. It uses your own `gh` login, not the `GITHUB_TOKEN` that `.env` may set for GHCR, because a registry-scoped token can't create environments.
6. **DNS.** In Cloudflare, add a proxied A record `infra-advisor-ai-dev` pointing at the dev UI Service's external IP (`kubectl get svc ui -n infra-advisor-dev`). It's a first-level subdomain of `bitsbone.com`, so Universal SSL covers it.
7. **Synthetics.** Create the `BASE_URL`, `SYNTHETIC_USER_EMAIL`, and secure `SYNTHETIC_USER_PASSWORD` global variables, put their ids in the test JSON, run `make create-synthetic-user ENV=prod`, and set the repo variable `SYNTHETICS_CONSULTANT_FLOW_ID` to the test's public id.

Dev is public, like prod, and its responses carry `X-Robots-Tag: noindex, nofollow` so search engines don't index it.

## The 2026-10-09 cutover

Before 2026-10-09, prod reported `env:dev`. History before that date stays under `env:dev`, so when you compare across the cutover, query `env:dev` for older prod data and `env:prod` for newer data. After the cutover, `env:dev` means the dev namespace.

Datadog configuration scoped by environment needs a matching `prod` entry:

- **Feature Flags.** Prompt-version targeting is evaluated per environment. See [prompt targeting](/infra-advisor-ai/llm-engineering/monitoring/prompt-targeting/).
- **Dashboards.** Template variables that default to `env:dev` should default to `env:prod`.
- **Sensitive Data Scanner and retention filters.** Check any scanning group or filter that names `env:dev`.

## Verify

```bash
kubectl get deploy -n infra-advisor -L tags.datadoghq.com/env,tags.datadoghq.com/version
kubectl get deploy -n infra-advisor-dev -L tags.datadoghq.com/env,tags.datadoghq.com/version
curl -s https://infra-advisor-ai-dev.bitsbone.com/env-config.js
kubectl exec -n kafka kafka-cluster-kafka-pool-0 -- bin/kafka-consumer-groups.sh \
  --bootstrap-server localhost:9092 --describe --group infra-advisor-agent-api
```

Each prod row should show `prod` and a commit SHA, and each dev row `dev` and the SHA from the latest PR comment. `env-config.js` should return `{"env":"dev"}`. The consumer group should list only clients from `infra-advisor` pods. In Datadog, `env:dev` should contain only dev's services and RUM sessions, and prod's PostgreSQL and Redis should show no connections from dev pods.
