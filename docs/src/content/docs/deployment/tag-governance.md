---
title: Shared sandbox tag governance
description: How ts_creator/ts_team/ts_purpose tagging is enforced across the shared Azure sandbox subscription, and where this project's own responsibility ends
docType: reference
audience:
  - platform-engineer
  - maintainer
maturity: experimental
sidebar:
  order: 4
  label: Tag governance
---

This project's Azure resources live in a subscription shared with other projects. Without enforced ownership tags, an orphaned or costly resource in that subscription has no way to trace back to a responsible person or team. `infra/bicep/governance/` provisions a subscription-scoped Azure Policy initiative that closes that gap; this page explains the shared-versus-project responsibility boundary and how to operate it safely.

## Responsibility boundary

- **Shared governance** (`infra/bicep/governance/`) is deployed and assigned **once per sandbox subscription**, independently of any single project's own deployment. It is not part of `make deploy-infra` and must never be triggered by it.
- **Project integration** (`infra/bicep/main.bicep`'s `tsCreator`/`tsTeam`/`tsPurpose` parameters and `commonTags` object) is this project's own responsibility: tagging compliantly here means the project's resources already satisfy the shared policy before its own inheritance/remediation ever runs, rather than depending on remediation to fix them after the fact.

## Rollout order — audit before deny

The initiative's `requiredTagsEffect` parameter defaults to `Audit` and must stay there until compliance has been checked across the whole sandbox:

1. Deploy `infra/bicep/governance/main.bicep` with the default `Audit` effect.
2. Confirm compliance state: `az policy state summarize --policy-set-definition <initiative-id>` (or `az policy state list` for per-resource detail).
3. Only as a **separate, explicit redeploy**, set `requiredTagsEffect: 'Deny'` in `infra/bicep/governance/parameters/sandbox.bicepparam`. Never start a rollout in `Deny`.

## Required Azure roles

- Deploying the governance template requires subscription-level `Owner` or `Resource Policy Contributor` + `User Access Administrator` (to create the policy definitions/initiative/assignment and the remediation identity's role assignment).
- The remediation identity itself is granted only **Tag Contributor** at subscription scope — deliberately not `Contributor`, since tag remediation is the only thing it needs to do.

## The AKS boundary and the non-taggable resource matrix

AKS's node resource group (and everything inside it — the VMSS, NICs, load balancer, public IPs) is owned and re-managed by the AKS resource provider's own control loop, not by the project's Bicep or by this policy's remediation. Fighting that ownership (tagging those resources directly, or trying to remediate them) is not attempted — they're excluded via the governance template's `exemptResourceGroupNames` parameter instead. Each project adds its own AKS node RG name there as it comes online; this project's is `rg-tola-infra-advisor-ai-nodes` (see [resource group migration](/resource-group-migration/) for the ownership history).

| Resource | Taggable via this policy? | Why |
|---|---|---|
| Project resource group | Yes — required (`ts_creator`/`ts_team`), audited (`ts_purpose`) | Normal resource group |
| AKS cluster resource | Yes — inherited via `modify` | Normal taggable resource |
| AKS node pool (`agentPoolProfiles`) | Yes — set directly by this project's Bicep | ARM schema supports it; not policy-remediated |
| AKS node resource group, its VMSS, its NICs | No — excluded via `notScopes` | Owned by the AKS resource provider's own control loop |

## Verification

- `az policy state summarize --policy-set-definition <initiative-id>` — overall compliance percentage.
- `az policy state list --filter "PolicySetDefinitionId eq '<initiative-id>'" --query "[?complianceState=='NonCompliant']"` — which resources still need remediation.
- `az policy remediation list --resource-group rg-ts-tag-governance` (or the subscription-level equivalent) — status of the three remediation tasks (one per `ts_*` tag).
