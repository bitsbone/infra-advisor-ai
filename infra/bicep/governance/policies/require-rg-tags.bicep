// require-rg-tags.bicep — generic "require a non-empty tag on resource
// groups" policy definition, reusable for any tag name via the `tagName`
// parameter. Assigned three times by the initiative (ts_creator, ts_team as
// Deny-capable; ts_purpose as Audit-only) rather than writing one hardcoded
// definition per tag.
//
// `tags[parameters('tagName')]` is Azure Policy's own documented syntax for
// parameterizing a tag-key lookup inside a `field` expression — the policy
// engine parses the `parameters(...)` reference embedded in the field string
// itself at evaluation time; it is not a Bicep/ARM template function call
// and Bicep passes it through as a literal string unchanged. This mirrors
// how Microsoft's own built-in "Require a tag on resource groups" policy is
// written. Not yet validated against a live subscription — confirm with
// `az policy definition create` (or the what-if step in the parent plan)
// before enabling Deny.

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
                field: 'tags[parameters(\'tagName\')]'
                exists: 'false'
              }
              {
                field: 'tags[parameters(\'tagName\')]'
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
