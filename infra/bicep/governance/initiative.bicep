// initiative.bicep — bundles the require/inherit policy definitions into
// one reviewable policySetDefinition, applying each to all three governed
// tags (ts_creator, ts_team required/deny-capable; ts_purpose audit-only).

targetScope = 'subscription'

@description('Name for the policy set (initiative) definition')
param initiativeName string = 'ts-sandbox-tag-governance'

module requirePolicy 'policies/require-rg-tags.bicep' = {
  name: 'deploy-require-rg-tags-policy'
}

module inheritPolicy 'policies/inherit-tags-modify.bicep' = {
  name: 'deploy-inherit-tags-modify-policy'
}

resource initiative 'Microsoft.Authorization/policySetDefinitions@2021-06-01' = {
  name: initiativeName
  properties: {
    displayName: 'Shared sandbox tag governance'
    description: 'Requires ts_creator/ts_team on every resource group in the sandbox (audit-only until explicitly switched to deny), audits ts_purpose, and inherits any missing ts_* tag from the parent resource group onto taggable resources.'
    policyType: 'Custom'
    parameters: {
      requiredTagsEffect: {
        type: 'String'
        defaultValue: 'Audit'
        allowedValues: [
          'Audit'
          'Deny'
          'Disabled'
        ]
        metadata: {
          displayName: 'Effect for required tags (ts_creator/ts_team)'
        }
      }
    }
    policyDefinitions: [
      {
        policyDefinitionId: requirePolicy.outputs.policyDefinitionId
        policyDefinitionReferenceId: 'require-ts-creator'
        parameters: {
          tagName: { value: 'ts_creator' }
          effect: { value: '[parameters(\'requiredTagsEffect\')]' }
        }
      }
      {
        policyDefinitionId: requirePolicy.outputs.policyDefinitionId
        policyDefinitionReferenceId: 'require-ts-team'
        parameters: {
          tagName: { value: 'ts_team' }
          effect: { value: '[parameters(\'requiredTagsEffect\')]' }
        }
      }
      {
        policyDefinitionId: requirePolicy.outputs.policyDefinitionId
        policyDefinitionReferenceId: 'audit-ts-purpose'
        parameters: {
          tagName: { value: 'ts_purpose' }
          effect: { value: 'Audit' }
        }
      }
      {
        policyDefinitionId: inheritPolicy.outputs.policyDefinitionId
        policyDefinitionReferenceId: 'inherit-ts-creator'
        parameters: {
          tagName: { value: 'ts_creator' }
        }
      }
      {
        policyDefinitionId: inheritPolicy.outputs.policyDefinitionId
        policyDefinitionReferenceId: 'inherit-ts-team'
        parameters: {
          tagName: { value: 'ts_team' }
        }
      }
      {
        policyDefinitionId: inheritPolicy.outputs.policyDefinitionId
        policyDefinitionReferenceId: 'inherit-ts-purpose'
        parameters: {
          tagName: { value: 'ts_purpose' }
        }
      }
    ]
  }
}

output initiativeId string = initiative.id
output tagContributorRoleId string = inheritPolicy.outputs.tagContributorRoleId
// The three modify-policy reference IDs, so main.bicep can create exactly
// one remediation task per modify reference without repeating the literals.
output modifyPolicyReferenceIds array = [
  'inherit-ts-creator'
  'inherit-ts-team'
  'inherit-ts-purpose'
]
