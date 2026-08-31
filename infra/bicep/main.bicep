// main.bicep — InfraAdvisor AI root orchestration template
// Scope: subscription (creates the resource group, then deploys all modules)
//
// Deploy command:
//   az deployment sub create \
//     --location eastus \
//     --template-file infra/bicep/main.bicep \
//     --parameters infra/bicep/parameters/dev.bicepparam

targetScope = 'subscription'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Primary Azure region for all resources')
param location string = 'eastus'

@description('Environment name — used in resource names and tags (e.g. dev, staging, prod)')
@allowed(['dev', 'staging', 'prod'])
param environment string = 'dev'

@description('Number of nodes in the AKS system node pool')
@minValue(1)
@maxValue(10)
param aksNodeCount int = 3

@description('VM size for each AKS node')
param aksNodeVmSize string = 'Standard_D2s_v3'

// ---------------------------------------------------------------------------
// ACA agentic POC params — the secrets here (openAiApiKey, datadogApiKey,
// uiUsername/uiPassword, ddRum*) have NO default and must be passed via
// CLI --parameters at
// deploy time. Unlike every other secret in this repo (which lives in a K8s
// Secret, created separately via `make create-*-secret`, never touching
// Bicep), these three flow through Bicep because Microsoft.App/containerApps
// has no equivalent to a K8s secretKeyRef sourced from an out-of-band
// resource — see infra/bicep/modules/aca-agentic-poc.bicep for why. Never
// add these to a .bicepparam file.
// ---------------------------------------------------------------------------

@description('Azure region for the ACA agentic POC — deliberately separate from the shared `location` param. Azure Container Apps Consumption-plan environments run on hidden, Microsoft-managed AKS capacity; eastus (this repo\'s default region) returned AKSCapacityHeavyUsage on first deploy attempt, so this POC defaults to eastus2 instead, same mitigation Azure\'s own error message suggests ("consider creating new AKS clusters in a different region") — same reasoning as azure-openai.bicep\'s separate whisper account/region.')
param acaLocation string = 'eastus2'

@description('Container image reference for the ACA agentic POC apps (services/aca-agentic-poc-dotnet) — defaults to the :latest tag pushed by build-push.yml on every merge to main')
param acaContainerImage string = 'ghcr.io/bitsbone/infra-advisor-ai/aca-agentic-poc-dotnet:latest'

@description('Revision suffix for both ACA agentic POC Container Apps. Defaults to a fresh timestamp on every deployment — needed because :latest is a fixed string, so without a changing suffix ACA sees no diff in the container template and skips creating a new revision (confirmed: az containerapp update --image ...:latest alone did not roll a new revision), leaving the OLD image running even after a new one is pushed.')
param acaRevisionSuffix string = utcNow('yyyyMMddHHmmss')

// No registry credentials needed — the aca-agentic-poc-dotnet GHCR package
// is public (verified via an anonymous pull test), independent of the
// source repo's own visibility. If that ever changes, registryUsername/
// registryPassword params would need to come back on the module.

@description('Azure OpenAI API key for the ACA agentic POC apps (reuses the existing account) — pass via CLI --parameters, never commit')
@secure()
param acaOpenAiApiKey string = ''

@description('Datadog API key for the ACA agentic POC apps — pass via CLI --parameters, never commit')
@secure()
param acaDatadogApiKey string = ''

@description('Datadog site for the ACA agentic POC apps')
param acaDatadogSite string = 'us3.datadoghq.com'

@description('Basic Auth username gating the ACA agentic POC demo UI — pass via CLI --parameters, never commit')
@secure()
param acaUiUsername string = ''

@description('Basic Auth password gating the ACA agentic POC demo UI — pass via CLI --parameters, never commit')
@secure()
param acaUiPassword string = ''

@description('Datadog RUM Application ID for the ACA agentic POC demo UI — reuses the same RUM Application as services/ui. Pass via CLI --parameters, never commit')
@secure()
param acaDdRumApplicationId string = ''

@description('Datadog RUM Client Token for the ACA agentic POC demo UI — see acaDdRumApplicationId. Pass via CLI --parameters, never commit')
@secure()
param acaDdRumClientToken string = ''

@description('Datadog RUM site for the ACA agentic POC demo UI')
param acaDdRumSite string = 'us3.datadoghq.com'

@description('Deploy the ACA agentic POC module — false by default so a routine `make deploy-infra` run does not require the secrets above; set true (and pass the secrets) explicitly when actually deploying this POC')
param deployAcaAgenticPoc bool = false

// ---------------------------------------------------------------------------
// Resource group
// ---------------------------------------------------------------------------
// Live resources in rg-tola-infra-advisor-ai (audited 2026-07-31 via
// `az resource list` — exactly 5 resources, all managed by this template):
//   aks-infra-advisor-dev        — managed by this template (aks.bicep)
//   oai-infra-advisor-dev        — managed by this template (azure-openai.bicep)
//   srch-infra-advisor-dev       — managed by this template (azure-ai-search.bicep)
//   law-infra-advisor-dev        — managed by this template (monitoring.bicep)
//   stinfraadvdev                — managed by this template (azure-storage.bicep)
// No manual/orphaned resources remain — the previously-noted vnet01 and the
// pre-Bicep infra-advisor-openai/infra-advisor-search orphans have since been
// cleaned up. All deployments in this template use ARM's default Incremental
// mode (no module sets `mode: 'Complete'`), so `make deploy-infra` only
// creates/updates resources declared here — it cannot delete anything absent
// from the template, whether or not this inventory is current.
//
// Current live node RG: MC_rg-tola-infra-advisor-ai_aks-infra-advisor-dev_eastus
//   (immutable for existing cluster — contains VMs, NICs, LB, public IPs)

resource resourceGroup 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: 'rg-tola-infra-advisor-ai'
  location: location
  tags: {
    environment: environment
    project: 'infra-advisor-ai'
    managedBy: 'bicep'
  }
}

// ---------------------------------------------------------------------------
// Module: AKS cluster
// ---------------------------------------------------------------------------

module aks 'modules/aks.bicep' = {
  name: 'deploy-aks'
  scope: resourceGroup
  params: {
    location: location
    environment: environment
    nodeCount: aksNodeCount
    nodeVmSize: aksNodeVmSize
  }
}

// ---------------------------------------------------------------------------
// Module: Azure AI Search
// ---------------------------------------------------------------------------

module search 'modules/azure-ai-search.bicep' = {
  name: 'deploy-azure-ai-search'
  scope: resourceGroup
  params: {
    location: location
    environment: environment
  }
}

// ---------------------------------------------------------------------------
// Module: Azure OpenAI
// ---------------------------------------------------------------------------

module openAi 'modules/azure-openai.bicep' = {
  name: 'deploy-azure-openai'
  scope: resourceGroup
  params: {
    location: location
    environment: environment
  }
}

// ---------------------------------------------------------------------------
// Module: Kafka (Strimzi on AKS — placeholder, no Azure PaaS resource)
// ---------------------------------------------------------------------------

module kafka 'modules/kafka.bicep' = {
  name: 'deploy-kafka-placeholder'
  scope: resourceGroup
  params: {}
}

// ---------------------------------------------------------------------------
// Module: Redis (K8s Deployment — placeholder, no Azure PaaS resource)
// ---------------------------------------------------------------------------

module redis 'modules/redis.bicep' = {
  name: 'deploy-redis-placeholder'
  scope: resourceGroup
  params: {}
}

// ---------------------------------------------------------------------------
// Module: Azure Blob Storage (raw ingestion data, Spark output, knowledge docs)
// ---------------------------------------------------------------------------

module storage 'modules/azure-storage.bicep' = {
  name: 'deploy-azure-storage'
  scope: resourceGroup
  params: {
    location: location
    environment: environment
  }
}

// ---------------------------------------------------------------------------
// Module: Monitoring (Log Analytics workspace + Datadog DaemonSet note)
// ---------------------------------------------------------------------------

module monitoring 'modules/monitoring.bicep' = {
  name: 'deploy-monitoring'
  scope: resourceGroup
  params: {
    location: location
    environment: environment
  }
}

// ---------------------------------------------------------------------------
// Module: ACA agentic POC (opt-in — see deployAcaAgenticPoc above)
// ---------------------------------------------------------------------------

module acaAgenticPoc 'modules/aca-agentic-poc.bicep' = if (deployAcaAgenticPoc) {
  name: 'deploy-aca-agentic-poc'
  scope: resourceGroup
  params: {
    location: acaLocation
    environment: environment
    openAiEndpoint: openAi.outputs.endpoint
    openAiApiKey: acaOpenAiApiKey
    logAnalyticsCustomerId: monitoring.outputs.workspaceCustomerId
    logAnalyticsSharedKey: monitoring.outputs.workspaceSharedKey
    containerImage: acaContainerImage
    revisionSuffix: acaRevisionSuffix
    datadogApiKey: acaDatadogApiKey
    datadogSite: acaDatadogSite
    uiUsername: acaUiUsername
    uiPassword: acaUiPassword
    ddRumApplicationId: acaDdRumApplicationId
    ddRumClientToken: acaDdRumClientToken
    ddRumSite: acaDdRumSite
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

@description('Name of the AKS cluster')
output aksName string = aks.outputs.aksName

@description('Fully-qualified domain name of the AKS API server')
output aksFqdn string = aks.outputs.aksFqdn

@description('AKS-managed node resource group (contains VMs, NICs, LB, public IPs)')
output aksNodeResourceGroup string = aks.outputs.nodeResourceGroup

@description('Azure AI Search HTTPS endpoint')
output searchEndpoint string = search.outputs.endpoint

@description('Azure OpenAI HTTPS endpoint')
output openAiEndpoint string = openAi.outputs.endpoint

@description('Whisper-only Azure OpenAI account HTTPS endpoint (separate account/region — see azure-openai.bicep)')
output whisperEndpoint string = openAi.outputs.whisperEndpoint

@description('Kafka bootstrap servers (in-cluster, Strimzi on AKS)')
output kafkaBootstrapServers string = kafka.outputs.kafkaBootstrapServers

@description('Redis connection string (in-cluster K8s Deployment)')
output redisConnectionString string = redis.outputs.redisConnectionString

@description('Log Analytics workspace name (AKS diagnostics)')
output logAnalyticsWorkspaceName string = monitoring.outputs.workspaceName

@description('Azure Blob Storage account name')
output storageAccountName string = storage.outputs.storageAccountName

@description('Azure Blob Storage primary endpoint')
output storageBlobEndpoint string = storage.outputs.blobEndpoint

@description('ACA agentic POC — managed-OTel-agent-path Container App FQDN (empty unless deployAcaAgenticPoc=true)')
output acaManagedAppFqdn string = deployAcaAgenticPoc ? acaAgenticPoc.outputs.managedAppFqdn : ''

@description('ACA agentic POC — Datadog-sidecar-path Container App FQDN (empty unless deployAcaAgenticPoc=true)')
output acaSidecarAppFqdn string = deployAcaAgenticPoc ? acaAgenticPoc.outputs.sidecarAppFqdn : ''
