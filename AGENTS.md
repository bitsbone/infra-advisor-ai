## InfraAdvisor AI — Agent Context

Global infrastructure consulting firm AI assistant. Refer to `@docs/agent-guides/project-map.md` for architectural overview.

---

### Build & Verify Commands

- **Infrastructure:** `make deploy-infra` (Bicep IaC)
- **K8s Auth:** `make create-ghcr-secret` (Run before `deploy-k8s`)
- **Deployment:** `make deploy-k8s` (All manifests)
- **Testing:** `uv run pytest -x services/<service>/tests/`
- **Monitoring:** \* `kubectl get pods -n infra-advisor`
  - `kubectl logs -n infra-advisor deploy/<n> --tail=50`
- **Access:** `az aks get-credentials --resource-group rg-tola-infra-advisor-ai --name aks-infra-advisor`

---

### Workflow Orchestration (Senior Standards)

1.  **Plan Mode Default:** Enter plan mode for ANY task with 3+ steps or architectural decisions. Write detailed specs upfront. If logic goes sideways, **STOP** and re-plan immediately.
2.  **Subagent Strategy:** Offload research, parallel analysis, and exploration to subagents to keep the main context window clean. One specific task per subagent.
3.  **Verification Before Done:** Never mark a task complete without proving it works. Run tests, check logs, and diff behavior. Ask: _"Would a staff engineer approve this?"_
4.  **Demand Elegance:** For non-trivial changes, pause and seek the most elegant solution. If a fix feels hacky, refactor based on current knowledge rather than over-engineering.
5.  **Autonomous Bug Fixing:** When given a bug report, resolve it autonomously using logs and failing tests. Aim for zero context switching for the user.

---

### Task Management & Self-Improvement

1.  **Plan First:** Write actionable items to `tasks/todo.md` and verify with the user before implementation.
2.  **Track & Explain:** Mark items complete as you go and provide a high-level summary at each step.
3.  **Document Results:** Add a review section to `tasks/todo.md` upon completion.
4.  **Self-Improvement Loop:** After ANY user correction, update `tasks/lessons.md` with the pattern. Ruthlessly iterate on these lessons to prevent repeat mistakes.

---

### Key Constraints

- **Runtime:** All Python services use `uv`, **Python 3.12**, and `pyproject.toml`.
- **Feature documentation:** Every new or materially changed feature must update the public learning experience under `docs/src/content/docs`. Prefer improving an existing topic; create a page only for a durable, independently discoverable learning objective, experiment, workflow, or reference domain. Choose the structure that best fits the subject instead of forcing a universal template. Explain why the capability matters, how this project uses it, and how a reader can verify an observable result. Compare implementation paths only when the comparison teaches a meaningful difference. When a relationship, branch, or boundary is difficult to understand linearly, prefer an accessible interactive explorer or guided exercise that lets the reader inspect evidence; do not turn simple sequences into canvases or add decorative interactivity. Link to canonical Datadog and project references instead of duplicating exhaustive configuration, API inventories, code, or background. Label planned, partial, experimental, stable, and deprecated behavior honestly. Keep temporary implementation history, parity tracking, and migration notes in maintainer or innovation-lab content. Add a sidebar entry only for a durable new topic.
- **Public-repository security:** Never commit secrets, credentials, private endpoints, JWTs, real user data, Datadog API/application keys, or production-only values. Use environment variables, ignored local configuration, secret stores, and obvious placeholders. Public client-side identifiers such as Datadog RUM application IDs and client tokens may be committed only when the provider explicitly designs them to ship in client binaries; document that distinction and never substitute a privileged key.
- **Markdown formatting:** Do not hard-wrap Markdown prose. Write each paragraph or list item on one physical line; allow renderers to wrap it visually. Code blocks and tables may use the line structure required by their syntax.
- **Security:** Never hardcode server-side secrets. Use `os.environ["VAR_NAME"]` and fail fast.
- **Schema:** Do not modify NBI field names; use exact names from PRD Section 3.
- **Orchestration:** \* Namespace: `infra-advisor` (Exceptions: `kafka`, `datadog`).
  - Manifests: Must include `imagePullSecrets: [{name: ghcr-pull-secret}]`.
  - Registry: `ghcr.io/bitsbone/infra-advisor-ai/<service>:latest`.
- **Public ingress:** The UI pod's nginx is the only LoadBalancer. Adding a service with a public subpath requires (1) a `location ^~ /<subpath>/` block in `services/ui/nginx.conf`, (2) the upstream configured with its subpath (e.g. `MP_WEBROOT`, `--root-path`), and (3) a UI image rebuild (nginx.conf is `COPY`'d at build time). Full rules in `@docs/agent-guides/core-conventions.md` § Public ingress routing.

---

### Execution Phase

Implement phases sequentially. Check `@specs/` for the current phase task list.

> **Core Principles:** > \* **Simplicity First:** Impact minimal code.
>
> - **No Laziness:** Find root causes. No temporary fixes.
> - **Minimal Impact:** Only touch what is necessary. Avoid introducing regressions.
