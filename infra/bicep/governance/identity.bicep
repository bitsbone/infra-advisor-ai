// identity.bicep — the tag-remediation user-assigned managed identity.
// Split out from main.bicep because Microsoft.ManagedIdentity/
// userAssignedIdentities only deploys at resource group scope, while the
// rest of this governance template is subscription-scoped.

@description('Name of the user-assigned managed identity')
param identityName string

@description('Azure region for the identity')
param location string

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

output identityId string = identity.id
output principalId string = identity.properties.principalId
