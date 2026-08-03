# ACA agentic POC: managed OTel agent vs Datadog sidecar — todo

Plan: /Users/kyle.taylor/.claude/plans/wondrous-percolating-unicorn.md

## Phase 1 — Bicep module
- [x] `infra/bicep/modules/aca-agentic-poc.bicep`: shared Container Apps Environment (openTelemetryConfiguration → Datadog) + two Container Apps (`-managed` single-container, `-sidecar` two-container)
- [x] `infra/bicep/main.bicep`: wire module behind `deployAcaAgenticPoc` (default false) opt-in flag, new secure params (never in .bicepparam)
- [x] `infra/bicep/modules/monitoring.bicep`: new `workspaceSharedKey` output for reuse
- [x] `az bicep build`: compiles clean (2 non-blocking linter warnings)

## Phase 2 — Minimal .NET agentic app
- [x] `services/aca-agentic-poc-dotnet/`: Program.cs (one `/run` endpoint, one tool, ChatClientAgent), Observability/TelemetrySetup.cs (env-driven OTLP protocol, no hardcoded endpoint), Dockerfile, .dockerignore
- [x] `dotnet build -c Release`: 0 warnings, 0 errors
- [x] Local boot test with fake config: `/health` → 200
- [x] `docker build` + containerized boot test: `/health` → 200

## Phase 3 — Docs
- [x] New `### ACA Agentic POC (aca-agentic-poc.bicep)` section in `docs/src/content/docs/architecture/infrastructure.md` — comparison table, secrets-flow-through-Bicep callout, status note (code done, deployment pending)
- [x] `npx astro build` + `npm run check-links`: clean

## Phase 0 — Spike (NOT started — requires real cloud deployment + secrets)
- [ ] Deploy `aca-agentic-poc-managed` for real, fire a request, confirm trace lands in Datadog
- [ ] Deploy `aca-agentic-poc-sidecar` for real, fire a request, confirm trace lands in Datadog
- [ ] If either path doesn't work as documented, note the gap and adjust Bicep/app config accordingly

## Phase 4 — End-to-end verification (blocked on Phase 0)
- [ ] `make deploy-infra` with `deployAcaAgenticPoc=true` + real secrets
- [ ] `curl` both apps' `/run` endpoints
- [ ] Confirm full `invoke_agent → execute_tool → chat` span tree in Datadog for both
- [ ] Write up comparison findings in the docs section above

## Status

All code-level work (Bicep + .NET app + docs) is written and verified locally/in Docker — nothing has been deployed to Azure yet. Actual deployment requires:
- A Datadog API key
- GHCR credentials for image pull (or switch to a different registry)
- Building + pushing the container image
- Running `make deploy-infra` with `deployAcaAgenticPoc=true` and the above secrets passed via `--parameters` (never committed)

This is a real, billable Azure resource provisioning step — paused here for the user to decide when/how to proceed, per the "confirm before costly/hard-to-reverse actions" rule. No `az deployment` or `az containerapp` command has been run.
