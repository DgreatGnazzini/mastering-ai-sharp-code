// Azure OpenAI (chapter 14): Entra ID only (disableLocalAuth), reachable only through its private
// endpoint, and model versions pinned with NoAutoUpgrade, so a model changes only when this file does.
param location string
param tags object
param name string
param sku string
param chatDeployments array
param embeddingDeployment object
param userPrincipalIds array
param privateEndpointSubnetId string
param privateDnsZoneId string

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: name
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: name
    disableLocalAuth: true
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny' }
  }
}

// One deployment at a time: Azure OpenAI refuses concurrent changes to the same account.
@batchSize(1)
resource chat 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = [for d in chatDeployments: {
  parent: account
  name: d.name
  sku: { name: sku, capacity: d.capacity }
  properties: {
    model: { format: 'OpenAI', name: d.model, version: d.version }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}]

resource embedding 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: embeddingDeployment.name
  dependsOn: [chat]
  sku: { name: sku, capacity: embeddingDeployment.capacity }
  properties: {
    model: { format: 'OpenAI', name: embeddingDeployment.model, version: embeddingDeployment.version }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

// Cognitive Services OpenAI User: call deployments. Not Contributor, which could change them.
var openAiUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')

resource users 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in userPrincipalIds: {
  name: guid(account.id, principalId, openAiUser)
  scope: account
  properties: {
    roleDefinitionId: openAiUser
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

module endpoint 'private-endpoint.bicep' = {
  name: '${name}-pe'
  params: {
    location: location
    tags: tags
    name: 'pe-${name}'
    serviceId: account.id
    groupId: 'account'
    subnetId: privateEndpointSubnetId
    privateDnsZoneId: privateDnsZoneId
  }
}

output v1Endpoint string = '${account.properties.endpoint}openai/v1/'
