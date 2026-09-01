// adf-functions.bicep — Azure Function App hosting the ADF-invoked ingestion
// steps (services/adf-functions/), replacing the self-hosted Airflow DAGs.
//
// One Function App, many named HTTP-triggered functions — mirrors the old
// single-image-many-DAGs Airflow deployment model and keeps deploy to one
// `func azure functionapp publish` step. Consumption plan (Y1) per the
// explicit choice to keep this demo environment simple/cheap — the two
// long-running DAGs that would have pushed against Consumption's ~10 minute
// execution cap (twdb_water_plan_refresh, knowledge_base_init) were dropped
// from the migration entirely rather than justifying a Premium plan.

@description('Azure region for the Function App')
param location string

@description('Environment tag value (e.g. dev, staging, prod)')
param environment string

@description('Blob storage connection string — reuses the existing storage account (stinfraadv<env>) rather than provisioning a dedicated one, both for AzureWebJobsStorage and for the ingestion functions\' own raw-data/prepared-data container access')
@secure()
param storageConnectionString string

@description('Azure AI Search HTTPS endpoint')
param searchEndpoint string

@description('Azure AI Search admin key')
@secure()
param searchApiKey string

@description('Azure AI Search index name')
param searchIndexName string = 'infra-advisor-knowledge'

@description('Azure OpenAI HTTPS endpoint')
param openAiEndpoint string

@description('Azure OpenAI API key')
@secure()
param openAiApiKey string

@description('Azure OpenAI embedding deployment name')
param openAiEmbeddingDeployment string = 'text-embedding-3-small'

@description('EIA (eia.gov) free API key — required by the eia and public-docs pipelines; the public-docs EIA state-profile fetcher skips gracefully (per its original Airflow behavior) if this is left empty')
@secure()
param eiaApiKey string = ''

@description('Datadog API key — Consumption-plan Functions have no agent sidecar, so traces/LLM Observability submit directly to Datadog intake (the "serverless compat" agentless model). Never pair with DD_AGENT_HOST.')
@secure()
param datadogApiKey string = ''

@description('Datadog site (e.g. us3.datadoghq.com)')
param datadogSite string = 'us3.datadoghq.com'

var functionAppName = 'func-adf-infra-advisor-${environment}'
var appServicePlanName = 'plan-adf-infra-advisor-${environment}'

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  tags: {
    environment: environment
    project: 'infra-advisor-ai'
  }
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'functionapp'
  properties: {
    reserved: true // required for Linux
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  tags: {
    environment: environment
    project: 'infra-advisor-ai'
  }
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'Python|3.12'
      appSettings: [
        { name: 'AzureWebJobsStorage', value: storageConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'python' }
        { name: 'AZURE_STORAGE_CONNECTION_STRING', value: storageConnectionString }
        { name: 'AZURE_SEARCH_ENDPOINT', value: searchEndpoint }
        { name: 'AZURE_SEARCH_API_KEY', value: searchApiKey }
        { name: 'AZURE_SEARCH_INDEX_NAME', value: searchIndexName }
        { name: 'AZURE_OPENAI_ENDPOINT', value: openAiEndpoint }
        { name: 'AZURE_OPENAI_API_KEY', value: openAiApiKey }
        { name: 'AZURE_OPENAI_EMBEDDING_DEPLOYMENT', value: openAiEmbeddingDeployment }
        { name: 'EIA_API_KEY', value: eiaApiKey }
        { name: 'DD_API_KEY', value: datadogApiKey }
        { name: 'DD_SITE', value: datadogSite }
        { name: 'DD_ENV', value: environment }
        { name: 'DD_SERVICE', value: 'infra-advisor-adf-functions' }
        { name: 'DD_VERSION', value: 'latest' }
        { name: 'DD_LLMOBS_ENABLED', value: 'true' }
        { name: 'DD_LLMOBS_ML_APP', value: 'infra-advisor-ai' }
      ]
    }
  }
}

@description('Function App name')
output functionAppName string = functionApp.name

@description('Function App default hostname (used as the ADF linked service base URL)')
output functionAppHostName string = functionApp.properties.defaultHostName

@description('Function App system-assigned managed identity principal ID (grant this Storage Blob Data Contributor if moving off connection-string auth later)')
output functionAppPrincipalId string = functionApp.identity.principalId

@description('Function App default host key — used by ADF\'s Function Activity linked service to authenticate (functions are deployed at AuthLevel.FUNCTION, not anonymous). Fully derivable from this resource within the same deployment, so it flows through Bicep module outputs only, matching the other listKeys()-derived outputs in this repo.')
output functionAppHostKey string = listkeys(resourceId('Microsoft.Web/sites/host', functionApp.name, 'default'), '2023-12-01').functionKeys.default

