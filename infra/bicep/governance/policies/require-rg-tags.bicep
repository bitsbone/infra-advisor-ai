// require-rg-tags.bicep — generic "require a non-empty tag on resource
// groups" policy definition, reusable for any tag name via the `tagName`
// parameter. Assigned three times by the initiative (ts_creator, ts_team as
// Deny-capable; ts_purpose as Audit-only) rather than writing one hardcoded
// definition per tag.
//
// A parameterized tag-key lookup inside a `field` expression must be
// wrapped as a real ARM template expression — `[concat('tags[', parameters
// ('tagName'), ']')]` — evaluated once at assignment time to produce the
// literal field string (e.g. "tags[ts_creator]"). A bare string containing
// the text `parameters('tagName')` with no `[...]` wrapper does NOT count
// as using the parameter — confirmed live: `az deployment sub create`
// rejected an earlier version of this file with "UnusedPolicyParameters:
// tagName ... not used in the policy rule" for exactly that reason. The
// `effect` parameter below (`'[parameters(\'effect\')]'`) was already
// correctly wrapped and never hit this error, which is what pointed to
// the fix.

targetScope = 'subscription'

@description('Name for this policy definition')
param policyName string = 'ts-require-rg-tag'

resource policy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: policyName
  properties: {
    displayName: 'Require a non-empty tag on resource groups'
    description: 'Denies or audits a resource group create/update when the tag named by the tagName parameter is missing or empty.'
    policyType: 'Custom'
    mode: 'All'
    parameters: {
      tagName: {
        type: 'String'
        metadata: {
          displayName: 'Tag name'
          description: 'Name of the tag to require a non-empty value for (e.g. ts_creator)'
        }
      }
      effect: {
        type: 'String'
        defaultValue: 'Audit'
        allowedValues: [
          'Audit'
          'Deny'
          'Disabled'
        ]
        metadata: {
          displayName: 'Effect'
          description: 'Audit until the rollout is validated across the sandbox; switch to Deny in a separate, explicit change.'
        }
      }
    }
    policyRule: {
      if: {
        allOf: [
          {
            field: 'type'
            equals: 'Microsoft.Resources/subscriptions/resourceGroups'
          }
          {
            anyOf: [
              {
                field: '[concat(\'tags[\', parameters(\'tagName\'), \']\')]'
                exists: 'false'
              }
              {
                field: '[concat(\'tags[\', parameters(\'tagName\'), \']\')]'
                equals: ''
              }
            ]
          }
        ]
      }
      then: {
        effect: '[parameters(\'effect\')]'
      }
    }
  }
}

output policyDefinitionId string = policy.id
