# Migration: kyletaylored/infra-advisor-ai → bitsbone/infra-advisor-ai + infra-advisor-ai.bitsbone.com

Full plan: see approved plan (clean cutover, docs → bitsbone.github.io, origin remote repointed).
Superseded an earlier unauthorized draft of this file that assumed a dual-domain transition — disregard any prior revision.

## Phase 2 — GHCR image references
- [ ] `.env`, `.env.example`
- [ ] `Makefile` (AIRFLOW_IMAGE_REPOSITORY, GHCR_PREFIX, --docker-username x2)
- [ ] `.github/workflows/build-push.yml` (IMAGE_PREFIX, DD_GIT_REPOSITORY_URL)
- [ ] k8s deployment.yaml image fields (auth-api, agent-api, agent-api-dotnet, mcp-server, mcp-server-dotnet, ui, load-generator)
- [ ] `k8s/airflow/values.yaml` repository
- [ ] `k8s/secrets/ghcr-pull-secret.yaml` comment
- [ ] `infra/bicep/main.bicep` acaContainerImage default
- [ ] README.md, AGENTS.md, CLAUDE.md
- [ ] .claude/agents/security-audit.md, .codex/agents/security-audit.toml

## Phase 3 — GitHub repo/org + docs site
- [ ] `.pages.yml`
- [ ] `docs/astro.config.mjs` (site, social links, og/twitter image)
- [ ] README.md / AGENTS.md / CLAUDE.md remaining links

## Phase 4 — App domain references
- [ ] `k8s/auth-api/configmap.yaml` (APP_BASE_URL, ALLOWED_ORIGINS)
- [ ] `k8s/airflow/values.yaml` (AIRFLOW__API__BASE_URL, AIRFLOW__WEBSERVER__BASE_URL)
- [ ] `k8s/ui/ingress.yaml` comment
- [ ] `.github/workflows/build-push.yml` minified-path-prefix
- [ ] `Makefile` echo messages
- [ ] mobile/README.md, mobile/OBSERVABILITY_PATTERNS.md, AppConfiguration.cs, .csproj default URL
- [ ] procurement-opportunities.v1.schema.json $id

## Not done in this coding session (external/manual)
- [ ] Phase 0: GitHub secrets re-add, GHCR PAT rotation, org package settings, About metadata, origin remote repoint (needs explicit user confirmation before running)
- [ ] Phase 1: Cloudflare DNS record creation/verification
- [ ] Phase 5: deploy + verification

# Shared Azure sandbox tag governance (Bicep authored; not yet deployed)

## Shared governance package
- [x] Create reusable subscription-scoped Azure Policy-as-Code, separate from InfraAdvisor's normal application deployment, so one reviewed initiative governs every project resource group in the sandbox. → `infra/bicep/governance/` (`main.bicep`, `initiative.bicep`, `identity.bicep`, `policies/`), never invoked by `make deploy-infra`.
- [x] Require non-empty `ts_creator` and `ts_team` on resource groups, audit non-empty `ts_purpose`, and roll the initiative out first in non-enforcing/audit mode before enabling deny behavior for required resource-group metadata. → `policies/require-rg-tags.bicep`, `requiredTagsEffect` param defaults to `Audit`.
- [x] Inherit each missing `ts_*` tag from the parent resource group onto taggable resources with `modify` and `mode: Indexed`, preserving intentional resource-level overrides. → `policies/inherit-tags-modify.bicep`.
- [x] Use one managed identity for the initiative, least-privilege Tag Contributor at subscription scope for remediation, deterministic RBAC names, and one remediation task per modify-policy reference for existing resources. → `identity.bicep` + `main.bicep`'s `tagContributorAssignment`/`remediations` (3 tasks, one per `ts_*` tag).
- [x] Provide explicit exclusions or exemptions for service-managed resource groups and document the unsupported-resource tag matrix rather than treating every Azure child resource as taggable. → `exemptResourceGroupNames` param (defaults to this project's AKS node RG) + the matrix table in `docs/.../deployment/tag-governance.md`.
- [ ] Package subscription and optional management-group entrypoints so the same definitions can govern one sandbox subscription now and additional sandbox subscriptions later without copying policy JSON. Partial: the templates are already parameterized/reusable across subscriptions (deploy the same files against a different subscription context); no separate management-group-scoped entrypoint exists yet — add one only if a second sandbox subscription actually materializes.

## Project integration
- [x] Add non-secret `TS_CREATOR`, `TS_TEAM`, and `TS_PURPOSE` deployment inputs with fail-fast validation; do not commit real user or team values. → `main.bicep` params (`@minLength(1)` on creator/team) + `Makefile`'s `check-env`/`deploy-infra` fail fast on empty.
- [x] Build a shared `commonTags` object in the InfraAdvisor Bicep root and apply it to the project resource group and every taggable first-party resource so initial deployment is compliant before policy evaluation completes. → `main.bicep`'s `commonTags` var, unioned into all 8 modules' tag blocks.
- [x] Propagate the shared tags through the AKS cluster and initial node-pool tag settings so AKS-managed resource-group, VMSS, and node resources receive supported custom tags without directly mutating AKS-managed resources. → `aks.bicep`'s `agentPoolProfiles[0].tags`; the node RG/VMSS/NICs themselves stay excluded (AKS-RP-owned), not force-tagged.
- [ ] Document a reusable tagged-resource-group bootstrap command/template for other projects. Partial: the pattern (params + `commonTags` + Makefile fail-fast) is documented conceptually in `tag-governance.md`, but no copy-paste boilerplate for a brand-new project exists yet.

## Documentation and verification
- [x] Update the existing public infrastructure/deployment documentation with the shared-versus-project responsibility boundary, one-time project metadata, policy rollout order, required Azure roles, AKS boundary, exemptions, and compliance verification commands. → new `docs/src/content/docs/deployment/tag-governance.md`; `architecture/infrastructure.md`'s tagging line updated to link it.
- [x] Compile the project and governance Bicep entrypoints and inspect the generated ARM for resource-group requirements, `Indexed` inheritance policies, subscription-scoped initiative assignment, managed identity, least-privilege role assignment, and remediation references. → `az bicep build` clean on both `infra/bicep/main.bicep` and `infra/bicep/governance/main.bicep` (pre-existing warnings only); generated ARM inspected for the expected 6 governance resources (RG, role assignment, policy assignment, remediation copy-loop, initiative + identity nested deployments).
- [x] Run Azure deployment validation/what-if and compliance queries when credentials and permissions are available; do not deploy, assign policy, create RBAC grants, remediate, or mutate the shared subscription without explicit authorization. **Done, with explicit user authorization** — `what-if` ran clean, then a real `az deployment sub create` was run. First attempt failed with `UnusedPolicyParameters` (the `tags[parameters('tagName')]` field syntax needed ARM-expression bracket-wrapping — `[concat('tags[', parameters('tagName'), ']')]` — confirming exactly the risk flagged below). Fixed both policy definitions, redeployed successfully: resource group, both policy definitions, the initiative, the policy assignment (confirmed running in `Audit` mode), the remediation identity + Tag Contributor role assignment, and all 3 remediation tasks are all live in the `datadog-ese-sandbox` subscription.

## Review
- Changed/new files: `infra/bicep/main.bicep`, all 8 `infra/bicep/modules/*.bicep`, new `infra/bicep/governance/{main,initiative,identity}.bicep` + `policies/{require-rg-tags,inherit-tags-modify}.bicep` + `parameters/sandbox.bicepparam`, `Makefile` (`check-env`/`deploy-infra`), new `docs/.../deployment/tag-governance.md`, `docs/.../architecture/infrastructure.md`.
- Verification evidence: both Bicep entrypoints compile clean (`az bicep build`, Bicep CLI 0.46.1) with only pre-existing warnings (secret-in-output linter notes, opt-in-module null-access notes) — nothing new introduced. Generated ARM for the governance template inspected directly (Python/json) and matches the intended resource shape.
- Rollout state: **deployed and live**, in the `datadog-ese-sandbox` subscription, in `Audit` mode (confirmed via `az rest` against the policyAssignments API — `requiredTagsEffect: Audit`). Switching to `Deny` is still an explicit, separate future redeploy per `tag-governance.md`'s rollout order — do not flip it without checking compliance state first.
- Remaining Azure permission requirements: confirmed the deploying user's existing permissions were sufficient (no explicit Owner/Resource Policy Contributor/User Access Administrator grant was needed beyond what was already there) — the remediation identity itself only got Tag Contributor, confirmed via `az role assignment list`.
- Exclusions/exemptions: `rg-tola-infra-advisor-ai-nodes` (this project's AKS node RG) via `exemptResourceGroupNames`.
- Resources confirmed non-taggable by design: AKS's node resource group, its VMSS, and its NICs — all owned by the AKS resource provider's own control loop, excluded via `notScopes` rather than fought.
- Both previously-flagged unverified syntax choices are now confirmed against the live subscription: the Tag Contributor role GUID (`4a9ae827-6dc8-4573-8ac7-8239d42aa03f`) was correct (role assignment succeeded, `az role assignment list` shows "Tag Contributor"). The `tags[parameters('tagName')]` field-expression syntax was **not** correct as originally written — the first real deploy attempt failed with `UnusedPolicyParameters` (a bare string containing `parameters('tagName')` isn't a real ARM expression). Fixed in both `policies/require-rg-tags.bicep` and `policies/inherit-tags-modify.bicep` by wrapping as a genuine ARM template expression: `[concat('tags[', parameters('tagName'), ']')]`. Redeployed successfully after the fix.

## Agent Observability trace list input/output (2026-10-09)

- [x] Python: `input_data`/`output_data` on `query-processing` workflow, router, and specialist agent spans
- [x] Python: move non-streaming `get_tools()` inside the workflow; wrap startup probe, `GET /tools`, `POST /tools/{name}` in named workflows (ddtrace MCP client-session spans otherwise surface as `read_stream` roots)
- [x] .NET: `query-processing` workflow root activity with `input.value`/`output.value`; restore `ExecutionContext` per iterator step so parenting and `gen_ai.conversation.id` survive `yield`
- [x] .NET: `EnableSensitiveData=true` (demo env, SDS redacts at intake)
- [x] Docs updated (python.mdx, dotnet.mdx, apm.md, agent-api-dotnet.md)

### Review
agent-api pytest 104 passed; .NET tests 39 passed / 1 skipped; docs build + link check clean (pre-existing tag-governance.md content error unrelated). Not yet deployed — verify in the LLM Obs trace list after rollout.

---

# Plan: separate prod and dev environments (drafted 2026-10-09, approved 2026-10-09, in progress)

**Goal:** Keep the live `infra-advisor` namespace as **prod** and add a shared **dev** namespace, `infra-advisor-dev`, on the same AKS cluster. Dev runs only the core app and APIs: ui, auth-api, agent-api, agent-api-dotnet, mcp-server and mcp-server-dotnet. It has its own seeded datastores and a dedicated synthetic-test user. PRs build images and can be deployed to dev for testing before merge.

**Out of scope for dev:** Kafka topics and consumers, load-generator, CronJobs, Airflow/ingestion, ADF Functions, the ACA POC, and AI Search index writes.

## Findings that shape the plan

- **Prod already calls itself `dev`.** `DD_ENV: "dev"` is set in every ConfigMap. `tags.datadoghq.com/env: dev` is set on every Deployment. `.NET OTEL_RESOURCE_ATTRIBUTES` uses `deployment.environment=dev`, the UI build uses `VITE_DD_ENV=dev`, and the DatadogAgent `global.tags: ["env:dev"]` is set cluster-wide (`datadog/datadog-agent.yaml:14-15`). Postgres DBM has `tags: ["env:dev"]`. Prod must be retagged to `env:prod` before dev exists, or the two can't be told apart.
- **Namespace is hardcoded everywhere.** `namespace: infra-advisor` appears in every manifest. In-cluster FQDNs (`*.infra-advisor.svc.cluster.local`) appear in the agent/auth ConfigMaps. `services/ui/nginx.conf` has six upstream FQDNs baked into the UI image. CI's `kubectl -n infra-advisor` is hardcoded at seven places in `build-push.yml`. Kustomize and Helm aren't used today.
- **RUM env is build-time only.** `services/ui/src/lib/datadog-rum.ts:19` reads `import.meta.env.VITE_DD_ENV`, baked in by `Dockerfile:7-19`. That means one UI image per env unless config moves to runtime.
- **The Kafka consumer group is shared.** Python and .NET both use the hardcoded group `infra-advisor-agent-api`. Kafka is plaintext and reachable from any namespace, so a dev agent pod would steal prod partitions. Neither service has a disable flag; an unset bootstrap falls back to the prod FQDN.
- **Auth isolation depends on `JWT_SECRET`.** agent-api validates the JWT signature only, so a shared secret would make prod tokens valid in dev and the reverse.
- **No synthetic user exists.** The Synthetics test `datadog/synthetics/consultant-query-flow.json` has no login step and fails at the login screen today. auth-api supports `BOOTSTRAP_ADMIN_*` and service accounts via `POST /admin/users` with `is_service_account=true`.
- **Schema self-initialises.** `users`, `conversations` and `messages` use idempotent DDL at startup, so an empty dev Postgres comes up ready to seed.
- **The AI Search corpus can't be regenerated.** The generator was retired, so dev must read prod's `infra-advisor-knowledge` index read-only.
- **Capacity is tight.** The cluster is 3× `Standard_D2s_v3` (6 vCPU), and the prod core already requests about 2.85 vCPU. Dev at one replica per service adds about 1.5 vCPU. Decision: keep three nodes, so the dev overlay lowers requests and dev pods are the ones that go Pending under pressure.
- **Mobile apps report `env:demo`.** iOS (`mobile/native/ios/Config/Shared.xcconfig:3`), Android (`mobile/native/android/gradle.properties:7`) and MAUI (`InfraAdvisor.Mobile.csproj:26`) all default to `demo` for every build type.

## Phase 1 — Kustomize base and overlays (refactor, zero prod diff)

- [x] Each `k8s/<svc>/` directory became a base with its own `kustomization.yaml`; files weren't moved, so CI path filters and docs links still work. `k8s/namespace.yaml` moved to `k8s/namespaces/`. `metadata.namespace` stays as `infra-advisor`, and the dev overlay overrides it.
- [x] Add `k8s/overlays/prod/`: namespace `infra-advisor`, current replicas/HPA and the LoadBalancer with its DNS label. It includes Kafka, load-generator and the other prod-only components.
- [x] Update `make deploy-k8s` to `kubectl apply -k k8s/overlays/$(ENV)`, with `ENV ?= prod`. Parameterize `check-pods` and the secret targets with `NAMESPACE` derived from `ENV`.
- [x] Verified: `kubectl kustomize k8s/overlays/prod` renders the same 39 objects as the old file list, identical field for field. `kubectl diff -k` against the live cluster equals the pre-existing drift exactly: CI image pins, the never-applied `postgres-allow-apps`/`redis-allow-apps` NetworkPolicies, the `infra-contract-awards-raw` KafkaTopic, and the Postgres `allowPrivilegeEscalation` securityContext.
- [x] Added `k8s/tests/test_overlays.py` (render + contract tests) and a `test-k8s` CI job. Switched CI and `redeploy-mailpit` from `apply -f <dir>` to `apply -k`.

## Phase 2 — Retag prod as `env:prod`

- [x] Base manifests now describe prod: `DD_ENV=prod`, `tags.datadoghq.com/env: prod`, DBM `env:prod`. `OTEL_RESOURCE_ATTRIBUTES` drops env/version because `TelemetrySetup.cs` already maps `DD_ENV`/`DD_VERSION` onto the resource. DBM `reported_hostname` stays `infra-advisor-postgres` for continuity (dev gets `-dev`). 
- [x] Remove `env:dev` from the DatadogAgent `global.tags`. Env comes from pod labels, so mixed `env:` values on infra metrics and logs go away.
- [x] Fix version tagging (`.github/scripts/pin-deployment.sh`: one strategic patch sets image, `DD_VERSION` and the version labels; rollback reuses it). Validated with `--dry-run=server` against prod. CI's `kubectl set image` never updates `DD_VERSION` or `tags.datadoghq.com/version`, which are stuck at `latest`. Set them to the short SHA in the same step.
- [x] Repo assets retagged (`datadog/**/*.json`). ADF Functions get `ddEnv` (default `prod`). UI build uses `VITE_DD_ENV=prod`. Live check: monitors are only *tagged* `env:dev` (no env-scoped queries); dashboard "Infra Advisor AI - Product Analytics" defaults `$env` to `dev`. **Manual after deploy:** set that default to `prod`, add a `prod` environment with the same allocations to each `__llmobs__.prompt.*` Feature Flag, check SDS/retention filters. Datadog follow-ups (manual): update the tags on the five dashboards, three monitors and the Synthetics test JSON. Check Datadog prompt-version Feature Flag targeting rules (keyed off `DD_ENV`) for `env:dev` conditions. Check SDS scanning-group and retention filters for env scoping. Historical data stays under `env:dev`, so note the cutover date in the docs.
- [x] **Mobile env (decided, verified via msbuild/xcodebuild/gradle).** Published/Release builds report `env:prod`; Debug and local builds report `env:dev`.
  - iOS: move `DD_ENV` from `Shared.xcconfig` into `Release.xcconfig` (`prod`) and `Debug.xcconfig` (`dev`).
  - Android: set `datadogEnv` per build type (`release` → `prod`, `debug` → `dev`) instead of one `gradle.properties` value.
  - MAUI: default `InfraAdvisorDatadogEnvironment` to `prod` when `Configuration == Release`, else `dev`; the `-p:` override keeps working. Update `ViewModelTests.cs:699` and the mobile READMEs.
  - The apps keep pointing at the prod API; a Debug build pointed at dev uses the existing `InfraAdvisorApiBaseUrl`-style overrides.

## Phase 3 — App changes (shipped to prod first, inert there)

- [x] **UI runtime config (one image for both envs).** Done: `services/ui/nginx/default.conf.template`, `/env-config.js`, `src/lib/app-config.ts`; UI Deployment sets `APP_ENV` from its own `tags.datadoghq.com/env` label and `K8S_NAMESPACE` from `metadata.namespace` (Downward API). Verified by running the image as prod and dev (env-config body, `Cache-Control: no-store`, rendered upstream FQDNs, `nginx -t`). Non-prod responses also carry `X-Robots-Tag: noindex, nofollow`.
  - Convert `nginx.conf` to `/etc/nginx/templates/default.conf.template`, which the nginx image runs through envsubst at start. Upstreams become `<svc>.${K8S_NAMESPACE}.svc.cluster.local`.
  - nginx serves `/env-config.js`, generated from a template, with `window.__APP_CONFIG__ = { env, ddEnv }`.
  - `datadog-rum.ts` reads `env` from it, falling back to `VITE_DD_ENV` for local dev.
  - Sourcemap upload keys on service + version, so it's unaffected.
  - The same image SHA can be promoted from dev to prod without a rebuild.
- [x] **RUM env tag (item 1).** RUM `env` comes from runtime config, so prod reports `env:prod` and dev reports `env:dev`. `allowedTracingUrls` is origin-relative already, so RUM↔APM correlation works on both hostnames.
- [x] **Non-prod banner (item 3).** `EnvironmentBanner.tsx`; `npm run build` passes. Not yet eyeballed in a browser — check on the first dev deploy.
  - Add a slim Chakra v3 banner, for example "DEV environment · `<short sha>` · test data only", in `App.tsx` above `AppInner`, so it shows on the login page and in chat. It renders only when `env !== "prod"`.
  - Replace the three hardcoded `h="100vh"` (`App.tsx:24`, `Chat.tsx:1146`, `LoginPage.tsx:93`) with a flex-column layout, so the banner doesn't cause overflow.
  - Keep the existing `data-testid`s.
- [x] **Kafka kill switch (item 4).** Python `kafka_config.py` (+ `tests/test_kafka_switch.py`, 11 tests incl. lifespan); .NET `KafkaSettings.cs` with conditional DI and a no-op publisher (+ startup test asserting no `KafkaConsumerService`). Prod ConfigMaps set `KAFKA_ENABLED: "true"` explicitly.
  - Add `KAFKA_ENABLED` (default `true`) to agent-api (consumer thread and contract-awards producer) and agent-api-dotnet (`KafkaConsumerService`, `ContractAwardsEventPublisher`).
  - When it's `false`, both no-op and log once at startup. Dev sets it to `false`.
  - Add tests in both stacks.
- [x] **auth-api.** Confirmed: `main.py:174` builds reset links from `APP_BASE_URL`; CORS from `ALLOWED_ORIGINS`. `APP_BASE_URL` and `ALLOWED_ORIGINS` are already env-driven, so no code change is needed. Confirm the reset-link host comes from config.

## Phase 4 — Dev overlay, datastores and seeding (item 5)

- [x] Namespace lives in `k8s/overlays/dev/namespace.yaml` (not the shared namespaces file, so a prod apply never creates it). CLAUDE.md/AGENTS.md updated; their stale `docs/agent-guides/` paths fixed. ~~Add `infra-advisor-dev` to `k8s/namespace.yaml`.~~ Update CLAUDE.md's namespace constraint to name it as an allowed exception.
- [x] Added `k8s/overlays/dev/` (9 Deployments, ~510m CPU requested; 11 contract tests in `k8s/tests`). In-namespace hostnames in the bases are now short Service names (`mcp-server`, `redis`, `mailpit`), verified from inside prod, so dev needs no hostname patches. `EVAL_SAMPLE_RATE` left as-is (dev traffic is tiny). **Cluster has no NetworkPolicy engine (`networkPolicy: none`)**, so namespace isolation is by config, not network. It includes the core services plus their own **postgres, redis and mailpit**, and not Kafka, load-generator or CronJobs. It sets:
  - `DD_ENV=dev`
  - one replica, with HPAs removed
  - `KAFKA_ENABLED=false`
  - `EVAL_SAMPLE_RATE` lowered
  - `K8S_NAMESPACE=infra-advisor-dev`
  - dev FQDNs for MCP, Redis and SMTP
  - `APP_BASE_URL` / `ALLOWED_ORIGINS` set to `https://infra-advisor-ai-dev.bitsbone.com`
  - nginx adds `X-Robots-Tag: noindex, nofollow` on dev, so search engines don't index the test environment
  - a UI LoadBalancer **without** the Azure DNS label, so it doesn't collide
  - NetworkPolicies copied for the new namespace
  - DBM `reported_hostname: infra-advisor-postgres-dev`
- [x] **Datastores.** `chat-media-dev` container added to `azure-storage.bicep` (needs `make deploy-infra`).
  - **Must be separate:** Postgres (its own PVC), Redis, Mailpit, `JWT_SECRET`, bootstrap admin, and Blob media in a new `chat-media-dev` container in the existing storage account (a small Bicep change to `azure-storage.bicep`).
  - **Shared read-only:** Azure OpenAI/Whisper (shared quota), the AI Search index (dev gets a **query key**, not the admin key), the public MCP data APIs, and the Prompt Registry.
- [x] **Secrets.** `ENV=dev` loads git-ignored `.env.dev` over `.env`; `deploy-k8s ENV=dev` refuses to run without it; non-prod `NAMESPACE` is forced. `create-synthetic-user-secret` added; pre-existing `create-mailpit-secret` bug fixed (`@kubectl` on a continued line). **Not run yet** (needs your secrets). Run `make create-secrets ENV=dev`, reusing the existing targets with `NAMESPACE=infra-advisor-dev`. Use a dev-specific `DATABASE_URL`, `JWT_SECRET`, Redis/Postgres/Mailpit passwords and search query key. Add a new `synthetic-user-secret`.
- [x] **Seed job.** `k8s/overlays/dev/seed/` (Job + script as ConfigMap, agent-api image) and `make seed-dev`. Demo users are service accounts at `@infra-advisor.test` (avoids fake `@datadoghq.com` addresses) sharing the synthetic password. **Not run yet.** Add `k8s/overlays/dev/seed-job.yaml` plus `scripts/seed_dev.py`. It is idempotent and safe to re-run, and is exposed as `make seed-dev`. It creates:
  - the bootstrap admin, via auth-api env
  - **the shared synthetic-test service account** (`is_service_account=true`, non-admin, fixed `job_role`), with email and password from `synthetic-user-secret`. Prod gets the same account with the same credentials (see Phase 6), so one Synthetics test runs against either env. The databases and `JWT_SECRET` stay separate, so a session from one env is still rejected by the other.
  - one demo user per `job_role`, for prompt-flag targeting tests
  - a few sample conversations, so the history UI isn't empty
  - No prod data is copied.
- [x] **Datadog Agent.** `matchExpressions` In [infra-advisor, infra-advisor-dev]; server dry-run OK. Add `infra-advisor-dev` to the `infra-advisor-dotnet-security-target` `namespaceSelector`, so dev agent-api-dotnet gets the same AAP/profiler injection.
- [x] **CI RBAC.** `infra/scripts/setup-dev-deploy-identity.sh` / `make setup-dev-deploy-identity`: a separate Entra app with Cluster User + RBAC Writer on the dev namespace only, federated to GitHub environment `dev` (which overrides `AZURE_CLIENT_ID`). Running it was blocked as a permission grant — **you run it**. Today's CI principal is cluster-wide RBAC Cluster Admin, too broad for PR code. Give the CI principal a namespace-scoped Azure RBAC role on `infra-advisor-dev` only. Add an OIDC federated credential for the GitHub environment `dev` (`repo:bitsbone/infra-advisor-ai:environment:dev`).

## Phase 5 — PR builds and deploy-to-dev

- [x] **Separate `deploy-dev.yml`, not `build-push.yml`.** Keeps prod's CI-gated path untouched. Builds all six core images at the PR head (GHA cache keeps unchanged ones cheap) tagged `<sha>` and `pr-<n>`, never `latest`. ~~`build-push.yml`.~~
  - Also run on PRs, either via a `pull_request` trigger or by dropping the `branches: [main]` filter on `workflow_run`.
  - PR images are tagged `pr-<n>` and `sha-<short>`, never `latest` (`is_default_branch` already guards that).
  - Main keeps building `latest` and `<sha>` and deploying to prod.
- [x] **New `deploy-dev` job.** Label `deploy:dev` (only that label's `labeled` event, plus `synchronize`/`reopened` while it's on) or `workflow_dispatch` with a PR number. Applies the overlay (minus the cluster-scoped Namespace), pins all six deployments to the PR SHA with `pin-deployment.sh`, waits with `rollout-or-rollback.sh`, runs the seed Job on the PR's agent-api image, comments on the PR. actionlint clean.
  - Triggered by the PR label `deploy:dev` or by `workflow_dispatch` with a PR number.
  - Runs in the GitHub environment `dev`: `kubectl apply -k k8s/overlays/dev`, then `set image` to the PR SHA, then rollout wait with automatic rollback (reusing the prod job's logic with a parameterized namespace).
  - Posts a PR comment with the dev URL, the deployed SHA and a link to the Datadog `env:dev version:<sha>` view.
  - Uses a `concurrency: deploy-dev` group, so the last deploy wins and the comment says which PR currently holds dev.
- [x] Fork PRs are skipped (head repo check, and `isCrossRepository` for dispatch). They build images but can't deploy (document this).

## Phase 6 — DNS, Cloudflare and Synthetics (item 2)

- [x] **Hostname (decided).** One fixed dev hostname: `infra-advisor-ai-dev.bitsbone.com`. It's a first-level subdomain, so Cloudflare Universal SSL covers it with no extra certificate. A wildcard is only worth it with per-PR namespaces, which is out of scope.
- [ ] **Cloudflare (manual).** Add a proxied (orange-cloud) A record `infra-advisor-ai-dev` pointing at the dev UI LoadBalancer IP. Access stays public like prod (decided). Revisit in-app access controls and bot blocking later (see Follow-ups).
- [x] **Synthetic user in prod.** `make create-synthetic-user ENV=prod` (seed script in `SEED_SYNTHETIC_ONLY=1` mode over a port-forward; tested in `k8s/tests/test_seed_dev.py`). **Not run yet** (needs prod admin creds). Create the same service account in prod once, through auth-api `POST /admin/users` (`is_service_account=true`), with the credentials from the prod `synthetic-user-secret`. No prod data is touched otherwise. Exclude it from usage dashboards by `usr.email` or a `service_account` RUM attribute.
- [x] **Synthetics (one test, swappable base URL).** Login step (`#email`, `#password`, `button[type='submit']`) + `{{ BASE_URL }}`; `deploy-dev` runs it with `--override startUrl` and `variables.BASE_URL` once `vars.SYNTHETICS_CONSULTANT_FLOW_ID` is set. Global-variable ids in the JSON are placeholders (per-org).
  - Add a login step to `consultant-query-flow.json`, using `{{ SYNTHETIC_USER_EMAIL }}` and `{{ SYNTHETIC_USER_PASSWORD }}` (secure global variables, shared by both envs).
  - Replace `{{UI_BASE_URL}}` with a `{{ BASE_URL }}` global variable that defaults to prod, so the scheduled run keeps testing `https://infra-advisor-ai.bitsbone.com`.
  - The `deploy-dev` job runs the same test with `datadog-ci synthetics run-tests --override startUrl=https://infra-advisor-ai-dev.bitsbone.com` (plus the matching `BASE_URL` variable override) as a post-deploy gate. No second test definition to keep in sync.

## Phase 7 — Docs and verification

- [x] Docs: `deployment/environments.md` (new; full reference + first-time setup runbook), `deployment/kubernetes.md`, `deployment/index.md`, `deployment/quickstart.md`, `development/testing.md`, `observability/rum.md`, `services/ui.md`, `llm-engineering/monitoring/prompt-targeting.mdx`; stale `docs/agent-guides/` refs fixed in CLAUDE.md/AGENTS.md. Build + links clean. ~~Update `docs/src/content/docs`: an environments page (prod vs dev, what's shared, how to deploy a PR to dev, seeding, the synthetic user), the deployment architecture page, the RUM page (runtime env) and the services pages. Fix the stale `docs/agent-guides/` references in CLAUDE.md.
- [ ] **Verify prod:** in Datadog, APM/RUM/LLM Obs/infra/logs show `env:prod` with `version:<sha>`, with no `env:dev` data after the cutover. Prod has no banner.
- [ ] **Verify dev:** after a PR deploy, the banner shows. RUM, APM and LLM Obs show `env:dev` with the PR SHA. Login works with seeded users. The Synthetics test passes. The dev consumer group gets no Kafka partitions (`kafka-consumer-groups --describe`). Prod Postgres and Redis show no dev sessions or users.

## Decisions (2026-10-09)

1. **Dev hostname:** `infra-advisor-ai-dev.bitsbone.com`, covered by Universal SSL.
2. **Access:** dev is public like prod. In-app access controls and bot blocking are a later follow-up.
3. **Synthetic user:** one service account with the same credentials in both envs; Synthetics swaps a `BASE_URL` variable.
4. **Capacity:** keep three nodes; dev runs one replica with lowered requests.
5. **Mobile:** published/Release builds report `env:prod`; Debug/local builds report `env:dev`.
6. **Deploy trigger:** opt-in PR label `deploy:dev` (plus `workflow_dispatch`).

## Follow-ups (not in this plan)

- In-app access controls for both envs, for example invite-only sign-up or an allowlist.
- Bot blocking: Cloudflare Bot Fight Mode or WAF rules on both hostnames, plus a rate limit on `/api/auth/*`.

## Delivery

Each phase is its own PR, merged in order. Phases 1–3 are safe for prod: a zero-diff refactor, a retag, and inert code. Dev comes up in Phases 4–6.

## Review (2026-10-09)

**Delivered as six stacked local branches** (merge in order; none pushed): `feat/env-kustomize-base` → `feat/env-prod-retag` → `feat/env-app-runtime` → `feat/env-dev-overlay` → `feat/env-deploy-dev` → `feat/env-synthetics-docs`.

**Verified:** `k8s/tests` 16 passed (overlay contracts + seed script against a fake auth-api); agent-api pytest 115 passed; .NET 46 passed / 1 skipped; MAUI core 81 passed; mobile env resolved per configuration via msbuild/xcodebuild/gradle; UI image run as prod and dev (env-config, noindex, rendered upstreams, `nginx -t`); prod overlay renders identically to the old file list and its live diff equals the pre-existing drift; CI pin patch server-dry-run against prod; actionlint and Bicep build clean; docs build and links clean.

**Live (2026-10-09):** `infra-advisor-dev` exists with dev-only secrets (`.env.dev`, generated; AI Search query key `infra-advisor-dev`), datastores, MCP servers and auth-api running, DBM user created. `agent-api`, `agent-api-dotnet` and `ui` are **scaled to 0** because `:latest` (main) predates `KAFKA_ENABLED` and the runtime nginx template — the dev agents would join prod's consumer group and the old UI proxies to prod. The first `deploy:dev` run sets them back to 1 with PR images. Dev UI LoadBalancer IP: `57.151.78.84`.

**Findings outside the plan:**
- The cluster has no NetworkPolicy engine (`networkPolicy: none`); the policies in `k8s/` were never applied to prod either. Isolation is by config — documented.
- Prod agent-api pod `…-hznfd` left the Kafka consumer group on 2026-09-30 (`max.poll.interval.ms` exceeded) and never rejoined; the consumer thread doesn't recover. Pre-existing; worth a fix.
- Fixed in passing: `create-mailpit-secret` (`@kubectl` on a continued line) and `setup-dbm.sh` (`:'pw'` in `psql -c`).
- `check-env` requires `TS_CREATOR`/`TS_TEAM` (Bicep-only) for `deploy-k8s`.
- `deployment/tag-governance.md` content-check error predates this work.

**Dev deploy (2026-10-13):** `deploy:dev` run 38440094439 on PR #11 succeeded at `f048423`. Six app services at 1/1 with `env:dev` and `version:f048423` labels; `/env-config.js` returns `{"env":"dev"}` with `X-Robots-Tag: noindex, nofollow`; seed job created the synthetic user, five demo users and their conversations; dev agents log `Kafka disabled` and the consumer group holds only prod pods; APM shows `kube_namespace:infra-advisor-dev` spans as `env:dev version:f048423`. Three fixes were needed first: base images through `mirror.gcr.io` (Docker Hub 504/429), and lockfile installs for the UI (`npm ci` — driver.js 1.9.0 rejects `side: "over"`) and auth-api (`uv sync --frozen` — SQLAlchemy 2.1 defaults `postgresql://` to psycopg 3). `services/load-generator/Dockerfile` still installs without its lockfile.

**Remaining (needs you):** merge PRs #6–#11 in order, then verify prod reports `env:prod version:<sha>`; after #7 merges, the Datadog items in Phase 2 (Product Analytics `$env` default, `prod` Feature Flag environments, SDS check); copy `SYNTHETIC_USER_*` from `.env.dev` into `.env`, run `ADMIN_EMAIL=<prod admin> make create-synthetic-user ENV=prod`, create the Synthetics global variables and set `SYNTHETICS_CONSULTANT_FLOW_ID`; eyeball the dev banner at https://infra-advisor-ai-dev.bitsbone.com.
