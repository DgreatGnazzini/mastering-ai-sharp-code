// The container registry (chapter 14). Images are code, not data, so this is the one service that
// keeps a public endpoint: the GitHub-hosted runner pushes to it. It accepts only Entra ID: no admin
// user, and only the three managed identities may pull. (Anonymous pull is off by default; the
// property that turns it on exists only in preview API versions.)
param location string
param tags object
param name string
param pullPrincipalIds array

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
    policies: {
      retentionPolicy: { status: 'disabled' }
    }
  }
}

var acrPull = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in pullPrincipalIds: {
  name: guid(registry.id, principalId, acrPull)
  scope: registry
  properties: {
    roleDefinitionId: acrPull
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

output loginServer string = registry.properties.loginServer
output name string = registry.name
