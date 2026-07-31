// azure-openai.bicep — Azure OpenAI account for InfraAdvisor AI
// SKU: S0 (standard pay-as-you-go)
// Model deployments:
//   - gpt-4.1-mini          → agent LLM + faithfulness evaluator (default)
//   - text-embedding-3-small → embedding model for Azure AI Search vector indexing
//   - gpt-4.1               → full-capability agent option
//   - gpt-5.4-mini          → next-gen efficient agent option (GlobalStandard)
//
// A SECOND Cognitive Services account (whisperAccount) hosts the whisper
// deployment in a different region — whisper-001's "Standard" deployment SKU
// is not offered in every region (confirmed via
// `az rest ... /providers/Microsoft.CognitiveServices/locations/<region>/models`:
// eastus returns an empty `skus` array for whisper, i.e. no deployable SKU
// there at all). eastus2 does support it. Since a deployment's region is
// fixed to its parent account's region, this can't be fixed by changing the
// deployment resource alone — it needs its own account in a supported region.

@description('Azure region for the main OpenAI account (chat + embedding models)')
param location string

@description('Azure region for the Whisper-only OpenAI account — must support the whisper-001 Standard SKU (eastus does not; eastus2/westeurope/northcentralus/swedencentral do)')
param whisperLocation string = 'eastus2'

@description('Environment tag value (e.g. dev, staging, prod)')
param environment string

var openAiAccountName = 'oai-infra-advisor-${environment}'

resource openAiAccount 'Microsoft.CognitiveServices/accounts@2024-04-01-preview' = {
  name: openAiAccountName
  location: location
  tags: {
    environment: environment
    project: 'infra-advisor-ai'
  }
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAiAccountName
    publicNetworkAccess: 'Enabled'
    restore: false
  }
}

// gpt-4.1-mini — primary agent LLM (reasoning + synthesis)
// capacity unit = thousands of tokens per minute (TPM)
resource gpt41MiniDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: openAiAccount
  name: 'gpt-4.1-mini'
  sku: {
    name: 'Standard'
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4.1-mini'
      version: '2025-04-14'
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

// text-embedding-3-small — vector embedding for RAG pipeline
// Replaces text-embedding-ada-002: better quality, lower cost, same dimensions
resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: openAiAccount
  name: 'text-embedding-3-small'
  dependsOn: [
    gpt41MiniDeployment
  ]
  sku: {
    name: 'Standard'
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-small'
      version: '1'
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

// gpt-4.1 — full-capability agent option
resource gpt41Deployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: openAiAccount
  name: 'gpt-4.1'
  dependsOn: [
    embeddingDeployment
  ]
  sku: {
    name: 'Standard'
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4.1'
      version: '2025-04-14'
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

// gpt-5.4-mini — next-gen efficient agent option (GlobalStandard, 250K TPM)
resource gpt54MiniDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: openAiAccount
  name: 'gpt-5.4-mini'
  dependsOn: [
    gpt41Deployment
  ]
  sku: {
    name: 'GlobalStandard'
    capacity: 250
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5.4-mini'
      version: '2026-03-17'
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

// ---------------------------------------------------------------------------
// Whisper account — separate from openAiAccount; see header comment for why.
// ---------------------------------------------------------------------------

var whisperAccountName = 'oai-infra-advisor-whisper-${environment}'

resource whisperAccount 'Microsoft.CognitiveServices/accounts@2024-04-01-preview' = {
  name: whisperAccountName
  location: whisperLocation
  tags: {
    environment: environment
    project: 'infra-advisor-ai'
    purpose: 'whisper-transcription'
  }
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: whisperAccountName
    publicNetworkAccess: 'Enabled'
    restore: false
  }
}

// whisper — audio transcription (speech-to-text cascade for voice chat attachments)
resource whisperDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: whisperAccount
  name: 'whisper'
  sku: {
    name: 'Standard'
    capacity: 3
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'whisper'
      version: '001'
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

@description('Azure OpenAI account name')
output openAiName string = openAiAccount.name

@description('Azure OpenAI HTTPS endpoint')
output endpoint string = openAiAccount.properties.endpoint

@description('Whisper-only Azure OpenAI account HTTPS endpoint')
output whisperEndpoint string = whisperAccount.properties.endpoint
