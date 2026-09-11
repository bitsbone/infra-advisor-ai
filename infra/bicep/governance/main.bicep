// main.bicep — Shared Azure sandbox tag governance
// Scope: subscription. Deliberately separate from infra/bicep/main.bicep —
// that template provisions ONE project's resources; this one governs every
// project's resource groups in the shared sandbox subscription. See
// docs/src/content/docs/deployment/tag-governance.md for the full writeup.
//
// NOT deployed as part of `make deploy-infra`. Deploy explicitly and only
// with authorization, since this mutates shared, cross-project state:
//   az deployment sub create \
//     --location eastus \
//     --template-file infra/bicep/governance/main.bicep \
//     --parameters infra/bicep/governance/parameters/sandbox.bicepparam
//
// Rollout order (see tag-governance.md): deploy with requiredTagsEffect
// left at its default 'Audit', confirm compliance state across the sandbox
// via `az policy state summarize`, THEN — as a separate, explicit change —
// redeploy with requiredTagsEffect='Deny'. Never start in Deny.

targetScope = 'subscription'

@description('Azure region for this deployment\'s own resources (the remediation identity; policy definitions/assignments are regionless)')
param location string = 'eastus'

@description('Effect for the required-tag policies (ts_creator/ts_team) — start and validate in Audit before switching to Deny in a separate, explicit redeploy')
@allowed([
  'Audit'
  'Deny'
  'Disabled'
])
param requiredTagsEffect string = 'Audit'

@description('Resource group names excluded from this initiative entirely — service-managed groups whose resources are owned by their resource provider\'s own control loop, not remediable like a normal resource group (e.g. an AKS cluster\'s node resource group). Populate one entry per project as its AKS node RG (or equivalent) comes online.')
param exemptResourceGroupNames array = [
  'rg-tola-infra-advisor-ai-nodes'
]

var identityName = 'id-ts-tag-governance-remediation'
var assignmentName = 'ts-sandbox-tag-governance'
var governanceResourceGroupName = 'rg-ts-tag-governance'
// Literal, not read from initiative.outputs.modifyPolicyReferenceIds — a
// `for` loop over resources needs a value resolvable at the start of the
// deployment, and a module output isn't (BCP178). Must stay in sync with
// the three inherit-* policyDefinitionReferenceId values in initiative.bicep.
var modifyPolicyReferenceIds = [
  'inherit-ts-creator'
  'inherit-ts-team'
  'inherit-ts-purpose'
]

module initiative 'initiative.bicep' = {
  name: 'deploy-ts-tag-governance-initiative'
}

// Microsoft.ManagedIdentity/userAssignedIdentities only deploys at resource
// group scope (BCP135) — this template is otherwise subscription-scoped
// (policy definitions/initiatives/assignments and the subscription-scoped
// role assignment below all require that), so it provisions one small,
// dedicated resource group to host just the remediation identity.
resource governanceResourceGroup 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: governanceResourceGroupName
  location: location
  tags: {
    purpose: 'shared-sandbox-tag-governance'
    managedBy: 'bicep'
  }
}

module identityModule 'identity.bicep' = {
  name: 'deploy-ts-tag-governance-identity'
  scope: governanceResourceGroup
  params: {
    identityName: identityName
    location: location
  }
}

// Least-privilege: Tag Contributor only, at subscription scope, so the
// remediation identity can write tags via the modify policies' remediation
// tasks but nothing else. Deterministic name (guid() over stable inputs)
// so a redeploy doesn't create a duplicate role assignment.
resource tagContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, identityName, 'TagContributor')
  properties: {
    principalId: identityModule.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: initiative.outputs.tagContributorRoleId
  }
}

// The userAssignedIdentities dictionary key must be a resource ID resolvable
// at the start of the deployment (BCP120) — a module output isn't, even
// though the identity's *name* is a deploy-time literal. Building the ID
// directly via resourceId() (rather than identityModule.outputs.identityId)
// sidesteps that; only principalId (used above, for the actual RBAC grant)
// genuinely needs to come from the module after the resource exists.
var remediationIdentityResourceId = resourceId(subscription().subscriptionId, governanceResourceGroupName, 'Microsoft.ManagedIdentity/userAssignedIdentities', identityName)

resource assignment 'Microsoft.Authorization/policyAssignments@2022-06-01' = {
  name: assignmentName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${remediationIdentityResourceId}': {}
    }
  }
  properties: {
    displayName: 'Shared sandbox tag governance'
    description: 'Requires ts_creator/ts_team, audits ts_purpose, and inherits missing ts_* tags from each resource group onto its taggable resources.'
    policyDefinitionId: initiative.outputs.initiativeId
    notScopes: [for rg in exemptResourceGroupNames: subscriptionResourceId('Microsoft.Resources/resourceGroups', rg)]
    parameters: {
      requiredTagsEffect: { value: requiredTagsEffect }
    }
  }
  dependsOn: [
    tagContributorAssignment
    identityModule
  ]
}

// One remediation task per modify-policy reference in the initiative — each
// only remediates resources non-compliant under that specific policy
// reference, matching the todo.md plan's "one remediation task per
// modify-policy reference" requirement. Remediation tasks are NOT
// self-running on a schedule; trigger with
// `az policy remediation create --name <task-name> ...` (or re-run) after
// confirming compliance state, never automatically.
resource remediations 'Microsoft.PolicyInsights/remediations@2021-10-01' = [for refId in modifyPolicyReferenceIds: {
  name: 'remediate-${refId}'
  properties: {
    policyAssignmentId: assignment.id
    policyDefinitionReferenceId: refId
    resourceDiscoveryMode: 'ExistingNonCompliant'
  }
}]

output remediationIdentityPrincipalId string = identityModule.outputs.principalId
output policyAssignmentId string = assignment.id
