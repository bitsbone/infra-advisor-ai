using '../main.bicep'

// Left in Audit — rollout order requires validating compliance across the
// sandbox before a separate, explicit redeploy switches this to Deny. See
// docs/src/content/docs/deployment/tag-governance.md.
param requiredTagsEffect = 'Audit'

// One entry per project's AKS node RG (or other service-managed group) —
// add this project's when it's known to differ from the default below.
param exemptResourceGroupNames = [
  'rg-tola-infra-advisor-ai-nodes'
]
