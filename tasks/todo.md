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
