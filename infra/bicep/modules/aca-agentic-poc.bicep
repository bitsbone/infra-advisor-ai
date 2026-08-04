// aca-agentic-poc.bicep — Two Azure Container Apps running the SAME minimal
// .NET agentic app image (services/aca-agentic-poc-dotnet/), each
// demonstrating a DIFFERENT OpenTelemetry → Datadog path — see
// docs/agent-guides/architecture/infrastructure.md for the write-up and the
// PRD this module implements (drafted for a customer .NET-on-ACA
// conversation, not a permanent product feature):
//
//   - aca-agentic-poc-managed — no sidecar. Relies entirely on THIS module's
//     Container Apps Environment `openTelemetryConfiguration`, i.e. Azure's
//     built-in managed OpenTelemetry agent, routed to Datadog via
//     `dataDogConfiguration`. The platform auto-injects
//     OTEL_EXPORTER_OTLP_ENDPOINT — the app container does NOT set it.
//     Managed agent is gRPC-only, hence OTEL_EXPORTER_OTLP_PROTOCOL=grpc.
//
//   - aca-agentic-poc-sidecar — two containers per revision: `app` +
//     `datadog-sidecar` (datadog/serverless-init). The app explicitly sets
//     OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318, bypassing the
//     environment's managed agent entirely in favor of the sidecar's own
//     OTLP receiver. This is the path documented at
//     https://docs.datadoghq.com/serverless/azure_container_apps/sidecar/dotnet/,
//     configured for its OTLP settings rather than the dd-trace-dotnet
//     tracer install the same guide also shows.
//
// Both apps share ONE Container Apps Environment — the environment-level
// openTelemetryConfiguration only affects an app that doesn't override
// OTEL_EXPORTER_OTLP_ENDPOINT itself, so the sidecar app's explicit override
// keeps the two paths from interfering with each other even though they're
// in the same environment. This keeps the footprint to one environment
// instead of two.
//
// Reuses the existing resource group + Azure OpenAI account (azure-openai.bicep)
// and the existing Log Analytics workspace (monitoring.bicep, required by the
// Container Apps Environment's appLogsConfiguration) — provisions no new
// Azure OpenAI or Log Analytics resource.

@description('Azure region')
param location string

@description('Environment tag value (e.g. dev, staging, prod)')
param environment string

@description('Azure OpenAI endpoint to reuse (azure-openai.bicep output)')
param openAiEndpoint string

@description('Azure OpenAI API key for the existing account — pass at deploy time via CLI --parameters, never commit to a .bicepparam file')
@secure()
param openAiApiKey string

@description('Azure OpenAI chat deployment name to use (reuses an existing deployment — no new model deployment)')
param openAiDeployment string = 'gpt-4.1-mini'

@description('Log Analytics workspace Customer ID to reuse (monitoring.bicep output) — required by the Container Apps Environment')
param logAnalyticsCustomerId string

@description('Log Analytics workspace primary shared key to reuse (monitoring.bicep output) — pass at deploy time, never commit')
@secure()
param logAnalyticsSharedKey string

@description('Container image reference for BOTH Container Apps — same image, only Bicep/env-var config differs between them')
param containerImage string

@description('Container registry server (this repo\'s existing convention is GHCR)')
param registryServer string = 'ghcr.io'

@description('Registry username for image pull')
param registryUsername string

@description('Registry password/PAT for image pull — pass at deploy time, never commit')
@secure()
param registryPassword string

@description('Datadog API key — pass at deploy time, never commit. Backs BOTH the managed agent\'s dataDogConfiguration.key and the sidecar\'s DD_API_KEY')
@secure()
param datadogApiKey string

@description('Datadog site, e.g. us3.datadoghq.com — match this repo\'s existing DD_SITE value')
param datadogSite string = 'us3.datadoghq.com'

@description('Basic Auth username gating the demo UI on both Container Apps — pass at deploy time, never commit')
@secure()
param uiUsername string

@description('Basic Auth password gating the demo UI on both Container Apps — pass at deploy time, never commit')
@secure()
param uiPassword string

@description('Datadog RUM Application ID — reuses the SAME RUM Application as services/ui (infra-advisor-ui), just with a distinct `service` tag per app (set in-app from OTEL_SERVICE_NAME). Pass at deploy time, never commit.')
@secure()
param ddRumApplicationId string

@description('Datadog RUM Client Token — see ddRumApplicationId. Pass at deploy time, never commit.')
@secure()
param ddRumClientToken string

@description('Datadog RUM site, e.g. us3.datadoghq.com')
param ddRumSite string = 'us3.datadoghq.com'

var envName = 'cae-agentic-poc-${environment}'
var managedAppName = 'aca-agentic-poc-managed'
var sidecarAppName = 'aca-agentic-poc-sidecar'
var tags = {
  environment: environment
  project: 'infra-advisor-ai'
  purpose: 'aca-otel-datadog-poc'
}

// ─── Shared Container Apps Environment ────────────────────────────────────
// openTelemetryConfiguration here only serves aca-agentic-poc-managed — see
// module header for why the sidecar app is unaffected despite sharing this
// environment.

resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-10-02-preview' = {
  name: envName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsSharedKey
      }
    }
    openTelemetryConfiguration: {
      destinationsConfiguration: {
        dataDogConfiguration: {
          site: datadogSite
          key: datadogApiKey
        }
      }
      tracesConfiguration: {
        destinations: ['dataDog']
      }
      logsConfiguration: {
        destinations: ['dataDog']
      }
      metricsConfiguration: {
        destinations: ['dataDog']
      }
    }
  }
}

// ─── Container App #1: ACA managed OpenTelemetry agent path (no sidecar) ──

resource managedApp 'Microsoft.App/containerApps@2024-10-02-preview' = {
  name: managedAppName
  location: location
  tags: union(tags, { otelPath: 'managed-agent' })
  properties: {
    environmentId: containerAppsEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: [
        { name: 'registry-password', value: registryPassword }
        { name: 'openai-api-key', value: openAiApiKey }
        { name: 'ui-username', value: uiUsername }
        { name: 'ui-password', value: uiPassword }
        { name: 'dd-rum-application-id', value: ddRumApplicationId }
        { name: 'dd-rum-client-token', value: ddRumClientToken }
      ]
    }
    template: {
      containers: [
        {
          name: 'app'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'AZURE_OPENAI_ENDPOINT', value: openAiEndpoint }
            { name: 'AZURE_OPENAI_API_KEY', secretRef: 'openai-api-key' }
            { name: 'AZURE_OPENAI_DEPLOYMENT', value: openAiDeployment }
            // Standard OTel env vars only — no OTEL_EXPORTER_OTLP_ENDPOINT.
            // The platform auto-injects it, pointing at the environment's
            // managed collector, which forwards to Datadog per this
            // environment's openTelemetryConfiguration above.
            { name: 'OTEL_SERVICE_NAME', value: managedAppName }
            { name: 'OTEL_TRACES_EXPORTER', value: 'otlp' }
            { name: 'OTEL_METRICS_EXPORTER', value: 'otlp' }
            { name: 'OTEL_LOGS_EXPORTER', value: 'otlp' }
            { name: 'OTEL_EXPORTER_OTLP_PROTOCOL', value: 'grpc' } // managed agent is gRPC-only
            { name: 'DD_ENV', value: environment }
            { name: 'DD_VERSION', value: 'latest' }
            { name: 'POC_UI_USERNAME', secretRef: 'ui-username' }
            { name: 'POC_UI_PASSWORD', secretRef: 'ui-password' }
            { name: 'DD_RUM_APPLICATION_ID', secretRef: 'dd-rum-application-id' }
            { name: 'DD_RUM_CLIENT_TOKEN', secretRef: 'dd-rum-client-token' }
            { name: 'DD_RUM_SITE', value: ddRumSite }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

// ─── Container App #2: Datadog serverless-init sidecar path ──────────────

resource sidecarApp 'Microsoft.App/containerApps@2024-10-02-preview' = {
  name: sidecarAppName
  location: location
  tags: union(tags, { otelPath: 'serverless-init-sidecar' })
  properties: {
    environmentId: containerAppsEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: [
        { name: 'registry-password', value: registryPassword }
        { name: 'openai-api-key', value: openAiApiKey }
        { name: 'datadog-api-key', value: datadogApiKey }
        { name: 'ui-username', value: uiUsername }
        { name: 'ui-password', value: uiPassword }
        { name: 'dd-rum-application-id', value: ddRumApplicationId }
        { name: 'dd-rum-client-token', value: ddRumClientToken }
      ]
    }
    template: {
      containers: [
        {
          name: 'app'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'AZURE_OPENAI_ENDPOINT', value: openAiEndpoint }
            { name: 'AZURE_OPENAI_API_KEY', secretRef: 'openai-api-key' }
            { name: 'AZURE_OPENAI_DEPLOYMENT', value: openAiDeployment }
            // Explicit override — bypasses the environment's managed agent,
            // points straight at the datadog-sidecar container below over
            // localhost (same revision, same network namespace).
            { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: 'http://localhost:4318' }
            // .NET's OTLP exporter defaults to protocol=grpc when this is
            // unset (OTel spec default) — without this, the exporter would
            // try to gRPC-handshake against the sidecar's HTTP/protobuf
            // receiver on :4318 and fail silently (batch exporter swallows
            // the error; nothing shows up in console logs or Datadog).
            { name: 'OTEL_EXPORTER_OTLP_PROTOCOL', value: 'http/protobuf' }
            { name: 'OTEL_SERVICE_NAME', value: sidecarAppName }
            { name: 'DD_ENV', value: environment }
            { name: 'DD_VERSION', value: 'latest' }
            { name: 'POC_UI_USERNAME', secretRef: 'ui-username' }
            { name: 'POC_UI_PASSWORD', secretRef: 'ui-password' }
            { name: 'DD_RUM_APPLICATION_ID', secretRef: 'dd-rum-application-id' }
            { name: 'DD_RUM_CLIENT_TOKEN', secretRef: 'dd-rum-client-token' }
            { name: 'DD_RUM_SITE', value: ddRumSite }
          ]
        }
        {
          name: 'datadog-sidecar'
          image: 'index.docker.io/datadog/serverless-init:latest'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'DD_API_KEY', secretRef: 'datadog-api-key' }
            { name: 'DD_SITE', value: datadogSite }
            { name: 'DD_AZURE_SUBSCRIPTION_ID', value: subscription().subscriptionId }
            { name: 'DD_AZURE_RESOURCE_GROUP', value: resourceGroup().name }
            { name: 'DD_SERVICE', value: sidecarAppName }
            { name: 'DD_ENV', value: environment }
            { name: 'DD_VERSION', value: 'latest' }
            // OTLP receiver — verified against the installed serverless-init
            // image in the Phase 0 spike before this config is trusted; see
            // the PRD's flagged risk (public reports of OTLP-over-
            // serverless-init rough edges on other platforms).
            { name: 'DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_GRPC_ENDPOINT', value: '0.0.0.0:4317' }
            { name: 'DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_HTTP_ENDPOINT', value: '0.0.0.0:4318' }
            { name: 'DD_OTLP_CONFIG_TRACES_ENABLED', value: 'true' }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

@description('Container Apps Environment name')
output environmentName string = containerAppsEnv.name

@description('FQDN of the managed-agent-path Container App')
output managedAppFqdn string = managedApp.properties.configuration.ingress.fqdn

@description('FQDN of the sidecar-path Container App')
output sidecarAppFqdn string = sidecarApp.properties.configuration.ingress.fqdn
