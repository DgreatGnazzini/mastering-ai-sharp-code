// Key Vault (chapter 14). PriorAuthBot as deployed here stores nothing in it: every service it uses
// accepts the managed identity. It is here for the secret you cannot avoid, such as a third-party
// API key, which the container app reads as a Key Vault reference (see docs/chapters/ch14.md), so the
// value never appears in a template, a pipeline log, or the app's configuration.
param location string
param tags object
param name string
param tenantId string
param readerPrincipalIds array
param administratorPrincipalId string
param privateEndpointSubnetId string
param privateDnsZoneId string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take(name, 24)
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
  }
}

var secretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var administrator = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '00482a5a-887f-4fb3-b363-3b7fe8e74483')

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in readerPrincipalIds: {
  name: guid(vault.id, principalId, secretsUser)
  scope: vault
  properties: {
    roleDefinitionId: secretsUser
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

resource admin 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(administratorPrincipalId)) {
  name: guid(vault.id, administratorPrincipalId, administrator)
  scope: vault
  properties: {
    roleDefinitionId: administrator
    principalId: administratorPrincipalId
  }
}

module endpoint 'private-endpoint.bicep' = {
  name: '${name}-pe'
  params: {
    location: location
    tags: tags
    name: 'pe-${vault.name}'
    serviceId: vault.id
    groupId: 'vault'
    subnetId: privateEndpointSubnetId
    privateDnsZoneId: privateDnsZoneId
  }
}

output uri string = vault.properties.vaultUri
