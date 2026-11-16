// Azure AI Search (chapter 14, optional): Entra ID only and private. PriorAuthBot uses it when
// VectorStore:Provider is AzureAISearch (chapter 6).
param location string
param tags object
param name string
param contributorPrincipalIds array
param privateEndpointSubnetId string
param privateDnsZoneId string

resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'standard' }
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'disabled'
    replicaCount: 2
    partitionCount: 1
  }
}

var indexContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
var serviceContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')

resource indexRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in contributorPrincipalIds: {
  name: guid(search.id, principalId, indexContributor)
  scope: search
  properties: {
    roleDefinitionId: indexContributor
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

// The connector creates the index on first use, which needs the service contributor role.
resource serviceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in contributorPrincipalIds: {
  name: guid(search.id, principalId, serviceContributor)
  scope: search
  properties: {
    roleDefinitionId: serviceContributor
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
    serviceId: search.id
    groupId: 'searchService'
    subnetId: privateEndpointSubnetId
    privateDnsZoneId: privateDnsZoneId
  }
}

output endpoint string = 'https://${search.name}.search.windows.net'
