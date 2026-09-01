# Migration: kyletaylored/infra-advisor-ai → bitsbone/infra-advisor-ai + infra-advisor-ai.bitsbone.com

Full plan: see approved plan (clean cutover, docs → bitsbone.github.io, origin remote repointed).
Superseded an earlier unauthorized draft of this file that assumed a dual-domain transition — disregard any prior revision.

## Business-development contract-awards tool-call incident
- [x] Confirm the call path and required `query` validation boundary.
- [x] Add explicit required-parameter guidance to the specialist and MCP tool descriptions.
- [x] Preserve the guidance when Prompt Registry returns an older managed prompt.
- [x] Add regression coverage for the prompt contract and required input schema.

### Review
The Azure OpenAI call omitted `query` while supplying only optional filters, so MCP adapter validation failed before the tool implementation ran. The effective business-development prompt now explicitly requires and demonstrates `query`, including when managed prompts are enabled, while the MCP schema remains correctly strict.

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
