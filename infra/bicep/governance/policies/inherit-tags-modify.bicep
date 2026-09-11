// inherit-tags-modify.bicep — generic "inherit a tag from the parent
// resource group if missing" modify policy, reusable for any tag name via
// the `tagName` parameter (assigned three times by the initiative, once per
// ts_creator/ts_team/ts_purpose). mode: Indexed — only applies to resource
// types that actually support tags, so it naturally skips untaggable child
// resources rather than needing an explicit type allowlist.
//
// Only ADDS a missing tag — never overwrites a resource-level tag that
// already differs from the resource group's value, preserving intentional
// per-resource overrides (the `if.exists: false` condition gates the whole
// modify effect).
//
// `field` values use `[concat('tags[', parameters('tagName'), ']')]` — a
// real ARM template expression, not a bare string — see
// require-rg-tags.bicep for why the bracket-wrapping is required (confirmed
// live: the unwrapped form fails deployment with "UnusedPolicyParameters").
//
// The role assigned for remediation must be exactly Tag Contributor
// (least privilege — not Contributor). The GUID below is Azure's built-in
// Tag Contributor role.

targetScope = 'subscription'

@description('Name for this policy definition')
param policyName string = 'ts-inherit-rg-tag'

var tagContributorRoleId = '/providers/Microsoft.Authorization/roleDefinitions/4a9ae827-6dc8-4573-8ac7-8239d42aa03f'

resource policy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: policyName
  properties: {
    displayName: 'Inherit a tag from the resource group if missing'
    description: 'Adds the tag named by the tagName parameter to a taggable resource, copying its value from the resource\'s own resource group, only when the resource does not already carry that tag.'
    policyType: 'Custom'
    mode: 'Indexed'
    parameters: {
      tagName: {
        type: 'String'
        metadata: {
          displayName: 'Tag name'
          description: 'Name of the tag to inherit (e.g. ts_creator)'
        }
      }
    }
    policyRule: {
      if: {
        field: '[concat(\'tags[\', parameters(\'tagName\'), \']\')]'
        exists: 'false'
      }
      then: {
        effect: 'modify'
        details: {
          roleDefinitionIds: [
            tagContributorRoleId
          ]
          conflictEffect: 'audit'
          operations: [
            {
              operation: 'addOrReplace'
              field: '[concat(\'tags[\', parameters(\'tagName\'), \']\')]'
              value: '[resourcegroup().tags[parameters(\'tagName\')]]'
            }
          ]
        }
      }
    }
  }
}

output policyDefinitionId string = policy.id
output tagContributorRoleId string = tagContributorRoleId
