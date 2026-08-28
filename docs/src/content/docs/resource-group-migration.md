---
title: AKS Resource Group Boundaries
description: Understand the Azure resource group owned by the project and the node resource group owned by AKS
docType: maintainer
audience:
  - contributor
  - operator
maturity: stable
verifiedOn: 2026-08-27
---

An AKS deployment uses two resource groups with different owners. This is a platform boundary, not duplicate infrastructure.

| Resource group | Owner | Contains |
|---|---|---|
| Project resource group | This project's Bicep deployment | The AKS managed-cluster resource and other Azure services declared by the project |
| AKS node resource group | Azure Kubernetes Service | Node pools, network interfaces, disks, load balancer resources, and other cluster infrastructure |

Manage project resources through Bicep. Manage workloads and Services through Kubernetes. Do not edit resources inside the node resource group as if they were independent application infrastructure; AKS may replace or reconcile them.

## Why the names can differ

AKS generates an `MC_...` node resource group name unless a name is supplied when the cluster is created. The current Bicep module declares `rg-tola-infra-advisor-ai-nodes` as the desired name for a newly created cluster.

The node resource group name is immutable for an existing AKS cluster. Therefore, the declaration in Bicep does not prove that a live cluster already uses that name. The module comments record that the earlier cluster used the generated `MC_...` form.

Confirm live state before planning any change:

```bash
az aks show \
  --resource-group rg-tola-infra-advisor-ai \
  --name aks-infra-advisor-dev \
  --query nodeResourceGroup \
  --output tsv
```

## Treat a rename as cluster replacement

Changing only the displayed name is not an in-place operation. Adopting a different node resource group requires replacing the cluster, which can change public IPs and removes cluster-scoped state unless it is recreated.

Before authorizing a replacement, inventory:

- Kubernetes Secrets and the system that can recreate them
- Persistent volumes and any data that is not rebuilt from an external source
- Operators and Helm releases, including Datadog and Airflow
- Public LoadBalancer addresses and DNS dependencies
- Workload identities, role assignments, and external allowlists
- The order used to restore namespaces, infrastructure controllers, data services, and application workloads

Use the current Bicep, Kubernetes manifests, Helm values, and Makefile to build the replacement procedure. Do not reuse a dated list of live resources or credentials from this page.

## Decide whether replacement is worth it

A generated node resource group name is cosmetic. Replace the cluster only when there is a broader operational reason—such as a planned cluster rebuild, a required immutable setting, or a tested disaster-recovery exercise.

If replacement is justified, require a reviewed runbook with:

1. A current-state export and recovery owner for every stateful dependency.
2. A maintenance window and explicit rollback or abort point.
3. Infrastructure validation before workload restoration.
4. DNS and public-route verification after the new LoadBalancer is assigned.
5. Representative application, data-pipeline, and Datadog signal checks.

The general deployment sequence and ownership boundaries are documented in [Kubernetes deployment](/infra-advisor-ai/deployment/kubernetes/) and [Infrastructure](/infra-advisor-ai/architecture/infrastructure/).

## Historical note

An earlier version of this page mixed a point-in-time cloud inventory, credential rotation steps, cost estimates, and a cluster replacement recipe. Those details were intentionally removed because they could not establish current live state and made a cosmetic rename look like an approved migration. Git history remains available if the old investigation is needed as context; re-verify every assumption before acting on it.
