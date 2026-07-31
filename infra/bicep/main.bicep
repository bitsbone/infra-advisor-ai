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
