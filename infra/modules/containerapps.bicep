// The Container Apps environment, the API, and two jobs (chapter 14). The environment is internal:
// it has no public IP, and the API is reachable only from the virtual network (and networks peered
// with it, such as Deductible Dynamics' corporate network). Every setting PriorAuthBot needs is an
// ordinary environment variable, because none of them is a secret.
param location string
param tags object
param prefix string
param zoneRedundant bool
param infrastructureSubnetId string
param vnetId string
param workspaceId string
param appInsightsConnectionString string
param appInsightsName string
param registryServer string
param apiImage string
param stableRevision string
param identities object
param tenantId string
param apiClientId string
param groupNames object
param openAiEndpoint string
param chatDeployment string
param embeddingDeploymentName string
param approvedForPhi bool
param postgresHost string
param postgresDatabase string
param auditContainerUri string

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${prefix}'
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: infrastructureSubnetId
      internal: true
    }
    zoneRedundant: zoneRedundant
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    // Console logs go to Azure Monitor through the diagnostic setting below, so no workspace key is needed.
    appLogsConfiguration: { destination: 'azure-monitor' }
  }
}

resource environmentLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: environment
  properties: {
    workspaceId: workspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
  }
}

// Inside the network, *.<environment domain> resolves to the environment's internal load balancer.
// A module, because the zone's name is the environment's domain, which exists only once the
// environment does, and a resource name in this file must be known before deployment starts.
module environmentDns 'environment-dns.bicep' = {
  name: 'cae-${prefix}-dns'
  params: {
    tags: tags
    domain: environment.properties.defaultDomain
    staticIp: environment.properties.staticIp
    vnetId: vnetId
  }
}

var authority = '${az.environment().authentication.loginEndpoint}${tenantId}/v2.0'
var connectionString = 'Host=${postgresHost};Database=${postgresDatabase};Username=${identities.api.name};Ssl Mode=Require'
var groupSettings = [for group in items(groupNames): { name: 'Identity__Groups__${group.key}', value: group.value }]

var apiSettings = concat([
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'Azure__ManagedIdentityClientId', value: identities.api.clientId }
  { name: 'Azure__TenantId', value: tenantId }

  // Sign-in: Entra ID tokens for PriorAuthBot's app registration, and the app role every endpoint requires.
  { name: 'Auth__Mode', value: 'Entra' }
  { name: 'Auth__JwtBearer__Authority', value: authority }
  { name: 'Auth__JwtBearer__Audience', value: apiClientId }
  { name: 'Auth__RequiredRole', value: 'PriorAuthBot.User' }
  { name: 'Tools__Downstream__ClientId', value: apiClientId }
  { name: 'Mcp__Server__Resource', value: 'api://${apiClientId}' }

  // The model, by deployment name. Changing chatDeployment is a model rollout.
  { name: 'AI__Provider', value: 'AzureOpenAI' }
  { name: 'AI__AzureOpenAI__Endpoint', value: openAiEndpoint }
  { name: 'AI__AzureOpenAI__ChatDeployment', value: chatDeployment }
  { name: 'AI__AzureOpenAI__EmbeddingDeployment', value: embeddingDeploymentName }
  { name: 'AI__ApprovedForPhi', value: string(approvedForPhi) }

  // PostgreSQL with Entra ID: a user name and no password. The token is fetched by the managed identity.
  { name: 'VectorStore__Provider', value: 'Postgres' }
  { name: 'Postgres__Authentication', value: 'Entra' }
  { name: 'ConnectionStrings__priorauthbot', value: connectionString }
  { name: 'Audit__Store', value: 'Postgres' }
  { name: 'Audit__CreateSchema', value: 'false' }
  { name: 'Audit__Archive__Provider', value: 'Blob' }
  { name: 'Audit__Archive__ContainerUri', value: auditContainerUri }
  { name: 'Usage__Ledger', value: 'Postgres' }
  { name: 'Usage__CreateSchema', value: 'false' }

  // Traces, metrics, and logs to Application Insights, signed in with the managed identity.
  { name: 'Telemetry__AzureMonitor__ConnectionString', value: appInsightsConnectionString }
], groupSettings)

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${prefix}-api'
  location: location
  tags: union(tags, { 'azd-service-name': 'api' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identities.api.id}': {} }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      // Multiple: a new revision starts with no traffic, so the pipeline can test it before anyone uses it.
      activeRevisionsMode: 'Multiple'
      maxInactiveRevisions: 10
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        traffic: empty(stableRevision) ? [{ latestRevision: true, weight: 100 }] : [{ revisionName: stableRevision, weight: 100, label: 'stable' }]
      }
      registries: [{ server: registryServer, identity: identities.api.id }]
    }
    template: {
      // Provisioning re-applies the stable revision's own template (image, model, suffix), so it
      // never creates a revision of its own; only the pipeline's canary step does.
      revisionSuffix: empty(stableRevision) ? null : last(split(stableRevision, '--'))
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: apiSettings
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 5
              failureThreshold: 24
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 30
            }
            {
              // Ready once PostgreSQL answers and the first ingestion pass is done.
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        // Two replicas at all times: one can restart or move while the other answers.
        minReplicas: 2
        maxReplicas: 10
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '20' } } }]
      }
    }
  }
}

// Monitoring Metrics Publisher on Application Insights: without it, the Entra-authenticated exporter is refused.
resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

var metricsPublisher = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')

resource publisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, identities.api.principalId, metricsPublisher)
  scope: appInsights
  properties: {
    roleDefinitionId: metricsPublisher
    principalId: identities.api.principalId
    principalType: 'ServicePrincipal'
  }
}

// The migration job: the same image with one argument, run as the database administrator identity
// before a new revision starts. It is the only thing that can create or change a table.
resource migrate 'Microsoft.App/jobs@2024-03-01' = {
  name: 'caj-${take(prefix, 20)}-migrate'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identities.migrate.id}': {} }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registryServer, identity: identities.migrate.id }]
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: apiImage
          args: ['migrate']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
            { name: 'Azure__ManagedIdentityClientId', value: identities.migrate.clientId }
            { name: 'Postgres__Authentication', value: 'Entra' }
            { name: 'ConnectionStrings__priorauthbot', value: 'Host=${postgresHost};Database=${postgresDatabase};Username=${identities.migrate.name};Ssl Mode=Require' }
            { name: 'Migrate__AppRole', value: identities.api.name }
            { name: 'Migrate__AppPrincipalId', value: identities.api.principalId }
          ]
        }
      ]
    }
  }
}

// The eval job: the smoke test against a revision, or the eval suite against a model deployment,
// from inside the network, where the private endpoints are. The pipeline sets the image and the
// command for each run.
resource evalJob 'Microsoft.App/jobs@2024-03-01' = {
  name: 'caj-${take(prefix, 20)}-eval'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identities.eval.id}': {} }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 3600
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registryServer, identity: identities.eval.id }]
    }
    template: {
      containers: [
        {
          name: 'eval'
          image: apiImage
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID', value: identities.eval.clientId }
            { name: 'AZURE_TENANT_ID', value: tenantId }
            { name: 'PRIORAUTHBOT_EVAL_AZURE_OPENAI_ENDPOINT', value: openAiEndpoint }
            { name: 'PRIORAUTHBOT_EVAL_CHAT_DEPLOYMENT', value: chatDeployment }
            { name: 'PRIORAUTHBOT_EVAL_EMBEDDING_DEPLOYMENT', value: embeddingDeploymentName }
          ]
        }
      ]
    }
  }
}

output apiName string = api.name
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output environmentDomain string = environment.properties.defaultDomain
output migrateJobName string = migrate.name
output evalJobName string = evalJob.name
