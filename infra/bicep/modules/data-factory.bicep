// data-factory.bicep — Azure Data Factory replacing self-hosted Airflow.
//
// Datadog Data Jobs Monitoring now supports ADF (the project's only reason
// for choosing Airflow specifically was "required for the DJM story" — see
// specs/infraadvisor-prd.md). Migrating 6 of the 8 previously-live DAGs;
// twdb_water_plan_refresh and knowledge_base_init were dropped entirely per
// an explicit decision to keep this demo environment simple (see the
// migration plan for details) rather than justify a Premium Function plan
// for their long-running steps.
//
// Every pipeline is a thin ADF orchestration wrapper around Azure Function
// Activities that call into services/adf-functions/ (Consumption plan —
// see adf-functions.bicep). All triggers are created STOPPED, matching the
// old DAGs' is_paused_upon_creation=True safety default — start them only
// after validating against a temporary Search index (see the migration
// plan's validation/cutover section).
//
// Datadog DJM setup (custom RBAC role grant to the Datadog App Registration)
// is a manual az CLI step, not provisioned here — see the migration plan.

@description('Azure region for Data Factory')
param location string

@description('Environment tag value (e.g. dev, staging, prod)')
param environment string

@description('Function App default hostname (from adf-functions.bicep)')
param functionAppHostName string

@description('Function App host key (from adf-functions.bicep)')
@secure()
param functionAppHostKey string

@description('Azure AI Search HTTPS endpoint — used by the public-docs pipeline\'s idempotency-check Web Activity')
param searchEndpoint string

@description('Azure AI Search admin key — used by the public-docs pipeline\'s idempotency-check Web Activity')
@secure()
param searchApiKey string

@description('Azure AI Search index name')
param searchIndexName string = 'infra-advisor-knowledge'

var factoryName = 'adf-infra-advisor-${environment}'
var runIdExpr = '@pipeline().RunId'

resource dataFactory 'Microsoft.DataFactory/factories@2018-06-01' = {
  name: factoryName
  location: location
  tags: {
    environment: environment
    project: 'infra-advisor-ai'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {}
}

resource functionLinkedService 'Microsoft.DataFactory/factories/linkedservices@2018-06-01' = {
  parent: dataFactory
  name: 'ls-adf-functions'
  properties: {
    type: 'AzureFunction'
    typeProperties: {
      functionAppUrl: 'https://${functionAppHostName}'
      functionKey: {
        type: 'SecureString'
        value: functionAppHostKey
      }
    }
  }
}

// Note: the "shared index-search-shared activity" is inlined into every
// pipeline below rather than factored into a Bicep user-defined `func` —
// user-defined functions must be pure (no resource/module references), and
// each inlined copy needs functionLinkedService's reference.

// ---------------------------------------------------------------------------
// fema — daily 02:00 UTC
// ---------------------------------------------------------------------------
resource pipelineFema 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = {
  parent: dataFactory
  name: 'pl-fema-refresh'
  properties: {
    activities: [
      {
        name: 'fetch-and-store-fema'
        type: 'AzureFunctionActivity'
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'fetch-and-store-fema'
          method: 'POST'
          body: { run_id: runIdExpr }
        }
      }
      {
        name: 'index-search-shared'
        type: 'AzureFunctionActivity'
        dependsOn: [
          { activity: 'fetch-and-store-fema', dependencyConditions: ['Succeeded'] }
        ]
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'index-search-shared'
          method: 'POST'
          body: { prepared_blob_path: '@activity(\'fetch-and-store-fema\').output.prepared_blob_path' }
        }
      }
    ]
  }
}

resource triggerFema 'Microsoft.DataFactory/factories/triggers@2018-06-01' = {
  parent: dataFactory
  name: 'tr-fema-refresh-daily'
  properties: {
    type: 'ScheduleTrigger'
    typeProperties: {
      recurrence: {
        frequency: 'Day'
        interval: 1
        startTime: '2026-01-01T02:00:00Z'
        timeZone: 'UTC'
        schedule: { hours: [2], minutes: [0] }
      }
    }
    pipelines: [
      { pipelineReference: { referenceName: pipelineFema.name, type: 'PipelineReference' }, parameters: {} }
    ]
    // runtimeState is a read-only, ARM-managed property (confirmed via
    // `az bicep build` — BCP073). New triggers always deploy stopped by
    // default, matching the old DAGs' is_paused_upon_creation=True safety
    // default with no action needed here. Starting one is a separate,
    // imperative operation after validation: `az datafactory trigger start
    // --resource-group rg-tola-infra-advisor-ai --factory-name <factory>
    // --name <trigger-name>`.
  }
}

// ---------------------------------------------------------------------------
// nbi — weekly Sunday 03:00 UTC
// ---------------------------------------------------------------------------
resource pipelineNbi 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = {
  parent: dataFactory
  name: 'pl-nbi-refresh'
  properties: {
    activities: [
      {
        name: 'fetch-and-store-nbi'
        type: 'AzureFunctionActivity'
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'fetch-and-store-nbi'
          method: 'POST'
          body: { run_id: runIdExpr }
        }
      }
      {
        name: 'index-search-shared'
        type: 'AzureFunctionActivity'
        dependsOn: [
          { activity: 'fetch-and-store-nbi', dependencyConditions: ['Succeeded'] }
        ]
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'index-search-shared'
          method: 'POST'
          body: { prepared_blob_path: '@activity(\'fetch-and-store-nbi\').output.prepared_blob_path' }
        }
      }
    ]
  }
}

resource triggerNbi 'Microsoft.DataFactory/factories/triggers@2018-06-01' = {
  parent: dataFactory
  name: 'tr-nbi-refresh-weekly'
  properties: {
    type: 'ScheduleTrigger'
    typeProperties: {
      recurrence: {
        frequency: 'Week'
        interval: 1
        startTime: '2026-01-04T03:00:00Z'
        timeZone: 'UTC'
        schedule: { hours: [3], minutes: [0], weekDays: ['Sunday'] }
      }
    }
    pipelines: [
      { pipelineReference: { referenceName: pipelineNbi.name, type: 'PipelineReference' }, parameters: {} }
    ]
    // runtimeState is a read-only, ARM-managed property (confirmed via
    // `az bicep build` — BCP073). New triggers always deploy stopped by
    // default, matching the old DAGs' is_paused_upon_creation=True safety
    // default with no action needed here. Starting one is a separate,
    // imperative operation after validation: `az datafactory trigger start
    // --resource-group rg-tola-infra-advisor-ai --factory-name <factory>
    // --name <trigger-name>`.
  }
}

// ---------------------------------------------------------------------------
// eia — weekly Sunday 04:00 UTC
// ---------------------------------------------------------------------------
resource pipelineEia 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = {
  parent: dataFactory
  name: 'pl-eia-refresh'
  properties: {
    activities: [
      {
        name: 'fetch-and-store-eia'
        type: 'AzureFunctionActivity'
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'fetch-and-store-eia'
          method: 'POST'
          body: { run_id: runIdExpr }
        }
      }
      {
        name: 'index-search-shared'
        type: 'AzureFunctionActivity'
        dependsOn: [
          { activity: 'fetch-and-store-eia', dependencyConditions: ['Succeeded'] }
        ]
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'index-search-shared'
          method: 'POST'
          body: { prepared_blob_path: '@activity(\'fetch-and-store-eia\').output.prepared_blob_path' }
        }
      }
    ]
  }
}

resource triggerEia 'Microsoft.DataFactory/factories/triggers@2018-06-01' = {
  parent: dataFactory
  name: 'tr-eia-refresh-weekly'
  properties: {
    type: 'ScheduleTrigger'
    typeProperties: {
      recurrence: {
        frequency: 'Week'
        interval: 1
        startTime: '2026-01-04T04:00:00Z'
        timeZone: 'UTC'
        schedule: { hours: [4], minutes: [0], weekDays: ['Sunday'] }
      }
    }
    pipelines: [
      { pipelineReference: { referenceName: pipelineEia.name, type: 'PipelineReference' }, parameters: {} }
    ]
    // runtimeState is a read-only, ARM-managed property (confirmed via
    // `az bicep build` — BCP073). New triggers always deploy stopped by
    // default, matching the old DAGs' is_paused_upon_creation=True safety
    // default with no action needed here. Starting one is a separate,
    // imperative operation after validation: `az datafactory trigger start
    // --resource-group rg-tola-infra-advisor-ai --factory-name <factory>
    // --name <trigger-name>`.
  }
}

// ---------------------------------------------------------------------------
// samgov (USASpending.gov) — weekly Sunday 06:00 UTC
// ---------------------------------------------------------------------------
resource pipelineSamgov 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = {
  parent: dataFactory
  name: 'pl-samgov-awards-refresh'
  properties: {
    activities: [
      {
        name: 'fetch-and-store-samgov'
        type: 'AzureFunctionActivity'
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'fetch-and-store-samgov'
          method: 'POST'
          body: { run_id: runIdExpr }
        }
      }
      {
        name: 'index-search-shared'
        type: 'AzureFunctionActivity'
        dependsOn: [
          { activity: 'fetch-and-store-samgov', dependencyConditions: ['Succeeded'] }
        ]
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'index-search-shared'
          method: 'POST'
          body: { prepared_blob_path: '@activity(\'fetch-and-store-samgov\').output.prepared_blob_path' }
        }
      }
    ]
  }
}

resource triggerSamgov 'Microsoft.DataFactory/factories/triggers@2018-06-01' = {
  parent: dataFactory
  name: 'tr-samgov-awards-refresh-weekly'
  properties: {
    type: 'ScheduleTrigger'
    typeProperties: {
      recurrence: {
        frequency: 'Week'
        interval: 1
        startTime: '2026-01-04T06:00:00Z'
        timeZone: 'UTC'
        schedule: { hours: [6], minutes: [0], weekDays: ['Sunday'] }
      }
    }
    pipelines: [
      { pipelineReference: { referenceName: pipelineSamgov.name, type: 'PipelineReference' }, parameters: {} }
    ]
    // runtimeState is a read-only, ARM-managed property (confirmed via
    // `az bicep build` — BCP073). New triggers always deploy stopped by
    // default, matching the old DAGs' is_paused_upon_creation=True safety
    // default with no action needed here. Starting one is a separate,
    // imperative operation after validation: `az datafactory trigger start
    // --resource-group rg-tola-infra-advisor-ai --factory-name <factory>
    // --name <trigger-name>`.
  }
}

// ---------------------------------------------------------------------------
// census — monthly, 1st 07:00 UTC (fan-in: population + permits run in
// parallel, join into one prepared-records activity, then shared index step)
// ---------------------------------------------------------------------------
resource pipelineCensus 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = {
  parent: dataFactory
  name: 'pl-census-market-intelligence-refresh'
  properties: {
    activities: [
      {
        name: 'fetch-census-population'
        type: 'AzureFunctionActivity'
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'fetch-census-population'
          method: 'POST'
          body: { run_id: runIdExpr }
        }
      }
      {
        name: 'fetch-census-permits'
        type: 'AzureFunctionActivity'
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'fetch-census-permits'
          method: 'POST'
          body: { run_id: runIdExpr }
        }
      }
      {
        name: 'build-census-prepared-records'
        type: 'AzureFunctionActivity'
        dependsOn: [
          { activity: 'fetch-census-population', dependencyConditions: ['Succeeded'] }
          { activity: 'fetch-census-permits', dependencyConditions: ['Succeeded'] }
        ]
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'build-census-prepared-records'
          method: 'POST'
          body: {
            run_id: runIdExpr
            population_blob_path: '@activity(\'fetch-census-population\').output.blob_path'
            permits_blob_path: '@activity(\'fetch-census-permits\').output.blob_path'
          }
        }
      }
      {
        name: 'index-search-shared'
        type: 'AzureFunctionActivity'
        dependsOn: [
          { activity: 'build-census-prepared-records', dependencyConditions: ['Succeeded'] }
        ]
        linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
        typeProperties: {
          functionName: 'index-search-shared'
          method: 'POST'
          body: { prepared_blob_path: '@activity(\'build-census-prepared-records\').output.prepared_blob_path' }
        }
      }
    ]
  }
}

resource triggerCensus 'Microsoft.DataFactory/factories/triggers@2018-06-01' = {
  parent: dataFactory
  name: 'tr-census-refresh-monthly'
  properties: {
    type: 'ScheduleTrigger'
    typeProperties: {
      recurrence: {
        frequency: 'Month'
        interval: 1
        startTime: '2026-02-01T07:00:00Z'
        timeZone: 'UTC'
        schedule: { hours: [7], minutes: [0], monthDays: [1] }
      }
    }
    pipelines: [
      { pipelineReference: { referenceName: pipelineCensus.name, type: 'PipelineReference' }, parameters: {} }
    ]
    // runtimeState is a read-only, ARM-managed property (confirmed via
    // `az bicep build` — BCP073). New triggers always deploy stopped by
    // default, matching the old DAGs' is_paused_upon_creation=True safety
    // default with no action needed here. Starting one is a separate,
    // imperative operation after validation: `az datafactory trigger start
    // --resource-group rg-tola-infra-advisor-ai --factory-name <factory>
    // --name <trigger-name>`.
  }
}

// ---------------------------------------------------------------------------
// public_docs — weekly Sunday 02:00 UTC, idempotency-gated (native Web
// Activity + If-Condition ahead of the Function Activity, replicating the
// original DAG's "skip if index already has >= 200 non-synthetic docs" gate)
// ---------------------------------------------------------------------------
resource pipelinePublicDocs 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = {
  parent: dataFactory
  name: 'pl-public-docs-ingestion'
  properties: {
    activities: [
      {
        name: 'check-index-doc-count'
        type: 'WebActivity'
        typeProperties: {
          method: 'GET'
          url: '${searchEndpoint}/indexes/${searchIndexName}/docs/$count?api-version=2024-07-01&$filter=source%20ne%20%27synthetic%27'
          headers: {
            'api-key': searchApiKey
          }
        }
      }
      {
        name: 'count-below-threshold'
        type: 'IfCondition'
        dependsOn: [
          { activity: 'check-index-doc-count', dependencyConditions: ['Succeeded'] }
        ]
        typeProperties: {
          expression: {
            type: 'Expression'
            value: '@less(int(string(activity(\'check-index-doc-count\').output.response)), 200)'
          }
          ifTrueActivities: [
            {
              name: 'public-docs-report-builder'
              type: 'AzureFunctionActivity'
              linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
              typeProperties: {
                functionName: 'public-docs-report-builder'
                method: 'POST'
                body: { run_id: runIdExpr }
              }
            }
            {
              name: 'index-search-shared'
              type: 'AzureFunctionActivity'
              dependsOn: [
                { activity: 'public-docs-report-builder', dependencyConditions: ['Succeeded'] }
              ]
              linkedServiceName: { referenceName: functionLinkedService.name, type: 'LinkedServiceReference' }
              typeProperties: {
                functionName: 'index-search-shared'
                method: 'POST'
                body: { prepared_blob_path: '@activity(\'public-docs-report-builder\').output.prepared_blob_path' }
              }
            }
          ]
        }
      }
    ]
  }
}

resource triggerPublicDocs 'Microsoft.DataFactory/factories/triggers@2018-06-01' = {
  parent: dataFactory
  name: 'tr-public-docs-ingestion-weekly'
  properties: {
    type: 'ScheduleTrigger'
    typeProperties: {
      recurrence: {
        frequency: 'Week'
        interval: 1
        startTime: '2026-01-04T02:00:00Z'
        timeZone: 'UTC'
        schedule: { hours: [2], minutes: [0], weekDays: ['Sunday'] }
      }
    }
    pipelines: [
      { pipelineReference: { referenceName: pipelinePublicDocs.name, type: 'PipelineReference' }, parameters: {} }
    ]
    // runtimeState is a read-only, ARM-managed property (confirmed via
    // `az bicep build` — BCP073). New triggers always deploy stopped by
    // default, matching the old DAGs' is_paused_upon_creation=True safety
    // default with no action needed here. Starting one is a separate,
    // imperative operation after validation: `az datafactory trigger start
    // --resource-group rg-tola-infra-advisor-ai --factory-name <factory>
    // --name <trigger-name>`.
  }
}

@description('Data Factory name')
output factoryName string = dataFactory.name

@description('Data Factory system-assigned managed identity principal ID — grant this the custom Datadog DJM role if you decide to automate that grant later (currently a manual az CLI step per the migration plan)')
output factoryPrincipalId string = dataFactory.identity.principalId
