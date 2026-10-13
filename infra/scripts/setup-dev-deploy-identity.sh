#!/usr/bin/env bash
# One-time setup for the deploy-dev workflow job. Run it yourself, as someone
# allowed to create Entra ID app registrations and AKS role assignments. Safe
# to re-run.
#
# Creates a dedicated app for PR deploys and grants it the least it needs, so
# code from a pull request can never touch prod:
#   - Azure Kubernetes Service Cluster User Role on the cluster (fetch a kubeconfig)
#   - Azure Kubernetes Service RBAC Writer on namespaces/infra-advisor-dev only
#   - an OIDC federated credential for the GitHub environment "dev"
# Then creates that GitHub environment and sets its AZURE_CLIENT_ID secret,
# which overrides the repo-level (prod) value for jobs that run in it.
#
# Usage: make setup-dev-deploy-identity
set -euo pipefail

# Make exports everything in .env, including the GITHUB_TOKEN PAT used for
# GHCR. gh prefers that over your own login, and the PAT can't create
# environments or set their secrets (HTTP 403). Use your gh login instead.
unset GITHUB_TOKEN GH_TOKEN

RG="${RESOURCE_GROUP:-rg-tola-infra-advisor-ai}"
AKS="${AKS_NAME:-aks-infra-advisor-dev}"
REPO="${GITHUB_REPO:-bitsbone/infra-advisor-ai}"
APP_NAME="github-infra-advisor-ai-deploy-dev"
NAMESPACE="infra-advisor-dev"

aks_id=$(az aks show -g "$RG" -n "$AKS" --query id -o tsv)

app_id=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv)
[ -n "$app_id" ] || app_id=$(az ad app create --display-name "$APP_NAME" --query appId -o tsv)
az ad sp show --id "$app_id" >/dev/null 2>&1 || az ad sp create --id "$app_id" >/dev/null
sp_id=$(az ad sp show --id "$app_id" --query id -o tsv)

# This repo's OIDC subject uses GitHub's immutable-ID format
# (repo:<owner>@<owner-id>/<repo>@<repo-id>:...), like the prod credential.
owner_id=$(gh api "repos/$REPO" --jq .owner.id)
repo_id=$(gh api "repos/$REPO" --jq .id)
subject="repo:${REPO%%/*}@${owner_id}/${REPO##*/}@${repo_id}:environment:dev"
if ! az ad app federated-credential list --id "$app_id" --query "[?name=='github-environment-dev'].name" -o tsv | grep -q .; then
  az ad app federated-credential create --id "$app_id" --parameters "{\"name\":\"github-environment-dev\",\"issuer\":\"https://token.actions.githubusercontent.com\",\"subject\":\"$subject\",\"audiences\":[\"api://AzureADTokenExchange\"]}" >/dev/null
fi

az role assignment create --assignee-object-id "$sp_id" --assignee-principal-type ServicePrincipal \
  --role "Azure Kubernetes Service Cluster User Role" --scope "$aks_id" -o none
az role assignment create --assignee-object-id "$sp_id" --assignee-principal-type ServicePrincipal \
  --role "Azure Kubernetes Service RBAC Writer" --scope "$aks_id/namespaces/$NAMESPACE" -o none

gh api -X PUT "repos/$REPO/environments/dev" >/dev/null
gh secret set AZURE_CLIENT_ID --repo "$REPO" --env dev --body "$app_id"

echo "✓ deploy-dev identity ready: federated subject $subject"
echo "  Roles: Cluster User (cluster), RBAC Writer ($NAMESPACE only)"
