// dev.bicepparam — development environment parameters for InfraAdvisor AI
//
// Usage:
//   az deployment sub create \
//     --location eastus \
//     --template-file infra/bicep/main.bicep \
//     --parameters infra/bicep/parameters/dev.bicepparam

using '../main.bicep'

param location = 'eastus'
param environment = 'dev'
param aksNodeCount = 3
param aksNodeVmSize = 'Standard_D2s_v3'

// Generic, non-personal defaults — required (a .bicepparam file must assign
// every non-defaulted param in the referenced template itself; `az`'s
// `--parameters key=value` CAN override a value already present here, but
// can't fill a gap this file left unassigned, confirmed via `az bicep
// build-params`). Real interactive deploys override these via
// `make deploy-infra`'s TS_CREATOR/TS_TEAM env vars (see Makefile); this
// fallback exists so CI-triggered deploys and this file's own build-time
// validation don't depend on a real person's name being committed.
param tsCreator = 'unassigned'
param tsTeam = 'platform-engineering'
