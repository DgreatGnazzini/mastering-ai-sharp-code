// PriorAuthBot on Azure (chapter 14). Deployed into the resource group that infra/bootstrap/github-oidc.sh
// creates, by the Azure Developer CLI (`azd provision`) or the deploy workflow. Everything PriorAuthBot talks to signs in with Microsoft Entra ID: there is no key,
// password, or connection string with a secret in it anywhere in this template, and tests in
// tests/PriorAuthBot.Tests/Deployment/InfrastructureTests.cs keep it that way.
//
//   Container Apps (internal ingress) ── managed identity ──► Azure OpenAI          (private endpoint)
//                                                         ├► PostgreSQL + pgvector (private endpoint)
//                                                         ├► Blob Storage, audit   (private endpoint)
//                                                         ├► Key Vault             (private endpoint)
//                                                         └► Application Insights  (Entra ID ingestion)
targetScope = 'resourceGroup'

@minLength(1)
@maxLength(20)
@description('Name of the environment, such as dev or prod. Part of every resource name.')
param environmentName string

@description('Primary region. Pick one where your Azure OpenAI models and the Data Zone deployment type are available.')
param location string = resourceGroup().location

@description('Object ID of the person or pipeline running the deployment. Becomes a Key Vault administrator, for break-glass access.')
param principalId string = ''

@description('Entra ID tenant of the app registration (infra/entra/app-registration.bicep).')
param tenantId string = tenant().tenantId

@description('Application (client) ID of PriorAuthBot\'s app registration: the audience of user tokens and the client in the on-behalf-of flow.')
param apiClientId string

@description('The image to run. The deploy workflow passes the image it built and scanned; the first provision can use any image that answers /health/ready.')
param apiImage string

@description('The revision that keeps 100 percent of traffic while a new one is a canary. Empty sends traffic to the latest revision (first deployment).')
param stableRevision string = ''

@description('Entra ID group object IDs and the names PriorAuthBot\'s document ACLs use, such as { "<object-id>": "Clinical" }.')
param groupNames object = {}

@description('Chat model deployments. Each name carries the model version, so two versions can run side by side during a canary.')
param chatDeployments array = [
  {
    name: 'gpt-4-1-mini-2025-04-14'
    model: 'gpt-4.1-mini'
    version: '2025-04-14'
    capacity: 100
  }
]

@description('The chat deployment the API uses. Changing it is a model rollout: the deploy workflow treats it like any other release.')
param chatDeployment string = 'gpt-4-1-mini-2025-04-14'

@description('The embedding deployment. Changing the embedding model means re-indexing every document (chapter 15).')
param embeddingDeployment object = {
  name: 'text-embedding-3-small-1'
  model: 'text-embedding-3-small'
  version: '1'
  capacity: 100
}

@allowed(['DataZoneStandard', 'Standard', 'GlobalStandard', 'ProvisionedManaged', 'DataZoneProvisionedManaged'])
@description('Azure OpenAI deployment type. DataZoneStandard keeps processing inside the data zone (the United States or the European Union); GlobalStandard may process anywhere.')
param openAiSku string = 'DataZoneStandard'

@description('Set true only after the business associate agreement covers this subscription (chapter 4). Until then the PHI guard redacts before any request leaves.')
param approvedForPhi bool = false

@description('Deploy Azure AI Search as well. PriorAuthBot uses PostgreSQL with pgvector by default, which it needs anyway for the audit log.')
param deployAiSearch bool = false

@description('Where alerts are sent.')
param alertEmail string = ''

@description('Days the audit archive keeps every blob immutable. 2,190 days is six years, as in chapter 13.')
param auditRetentionDays int = 2190

@description('Zone-redundant PostgreSQL and Container Apps. Off for a dev environment, on for production.')
param zoneRedundant bool = false

var tags = { 'azd-env-name': environmentName, app: 'priorauthbot', chapter: 'ch14' }
var token = toLower(uniqueString(resourceGroup().id, environmentName))
var prefix = 'priorauthbot-${environmentName}'

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    location: location
    tags: tags
    name: 'vnet-${prefix}'
    aiSearch: deployAiSearch
  }
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    tags: tags
    workspaceName: 'log-${prefix}'
    appInsightsName: 'appi-${prefix}'
  }
}

module identities 'modules/identities.bicep' = {
  name: 'identities'
  params: {
    location: location
    tags: tags
    prefix: prefix
  }
}

module registry 'modules/registry.bicep' = {
  name: 'registry'
  params: {
    location: location
    tags: tags
    name: 'cr${replace(environmentName, '-', '')}${token}'
    pullPrincipalIds: [identities.outputs.apiPrincipalId, identities.outputs.migratePrincipalId, identities.outputs.evalPrincipalId]
  }
}

module openAi 'modules/openai.bicep' = {
  name: 'openai'
  params: {
    location: location
    tags: tags
    name: 'oai-${prefix}-${token}'
    sku: openAiSku
    chatDeployments: chatDeployments
    embeddingDeployment: embeddingDeployment
    userPrincipalIds: [identities.outputs.apiPrincipalId, identities.outputs.evalPrincipalId]
    privateEndpointSubnetId: network.outputs.privateEndpointSubnetId
    privateDnsZoneId: network.outputs.zones.openai
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgres'
  params: {
    location: location
    tags: tags
    name: 'psql-${prefix}-${token}'
    tenantId: tenantId
    adminPrincipalId: identities.outputs.migratePrincipalId
    adminPrincipalName: identities.outputs.migrateName
    zoneRedundant: zoneRedundant
    privateEndpointSubnetId: network.outputs.privateEndpointSubnetId
    privateDnsZoneId: network.outputs.zones.postgres
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    tags: tags
    name: 'st${replace(environmentName, '-', '')}${token}'
    retentionDays: auditRetentionDays
    writerPrincipalId: identities.outputs.apiPrincipalId
    privateEndpointSubnetId: network.outputs.privateEndpointSubnetId
    privateDnsZoneId: network.outputs.zones.blob
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    tags: tags
    name: 'kv-${take(replace(environmentName, '-', ''), 8)}-${token}'
    tenantId: tenantId
    readerPrincipalIds: [identities.outputs.apiPrincipalId]
    administratorPrincipalId: principalId
    privateEndpointSubnetId: network.outputs.privateEndpointSubnetId
    privateDnsZoneId: network.outputs.zones.vault
  }
}

module search 'modules/search.bicep' = if (deployAiSearch) {
  name: 'search'
  params: {
    location: location
    tags: tags
    name: 'srch-${prefix}-${token}'
    contributorPrincipalIds: [identities.outputs.apiPrincipalId]
    privateEndpointSubnetId: network.outputs.privateEndpointSubnetId
    privateDnsZoneId: network.outputs.zones.search
  }
}

module apps 'modules/containerapps.bicep' = {
  name: 'containerapps'
  params: {
    location: location
    tags: tags
    prefix: prefix
    zoneRedundant: zoneRedundant
    infrastructureSubnetId: network.outputs.appsSubnetId
    vnetId: network.outputs.vnetId
    workspaceId: monitoring.outputs.workspaceId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    appInsightsName: monitoring.outputs.appInsightsName
    registryServer: registry.outputs.loginServer
    apiImage: apiImage
    stableRevision: stableRevision
    identities: identities.outputs.all
    tenantId: tenantId
    apiClientId: apiClientId
    groupNames: groupNames
    openAiEndpoint: openAi.outputs.v1Endpoint
    chatDeployment: chatDeployment
    embeddingDeploymentName: embeddingDeployment.name
    approvedForPhi: approvedForPhi
    postgresHost: postgres.outputs.fqdn
    postgresDatabase: postgres.outputs.database
    auditContainerUri: storage.outputs.auditContainerUri
  }
}

module alerts 'modules/alerts.bicep' = if (!empty(alertEmail)) {
  name: 'alerts'
  params: {
    location: location
    tags: tags
    prefix: prefix
    email: alertEmail
    appInsightsId: monitoring.outputs.appInsightsId
  }
}

// azd reads these, and so does the deploy workflow.
output AZURE_RESOURCE_GROUP string = resourceGroup().name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.outputs.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = registry.outputs.name
output API_APP_NAME string = apps.outputs.apiName
output API_INTERNAL_URL string = apps.outputs.apiUrl
output CONTAINER_APPS_ENVIRONMENT_DOMAIN string = apps.outputs.environmentDomain
output MIGRATE_JOB_NAME string = apps.outputs.migrateJobName
output EVAL_JOB_NAME string = apps.outputs.evalJobName
output AZURE_OPENAI_ENDPOINT string = openAi.outputs.v1Endpoint
output API_MANAGED_IDENTITY_CLIENT_ID string = identities.outputs.apiClientId
output API_MANAGED_IDENTITY_PRINCIPAL_ID string = identities.outputs.apiPrincipalId
output EVAL_MANAGED_IDENTITY_CLIENT_ID string = identities.outputs.all.eval.clientId
output LOG_ANALYTICS_WORKSPACE_GUID string = monitoring.outputs.workspaceGuid
output EMBEDDING_DEPLOYMENT string = embeddingDeployment.name
output POSTGRES_HOST string = postgres.outputs.fqdn
output AUDIT_STORAGE_ACCOUNT string = storage.outputs.accountName
