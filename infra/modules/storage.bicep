// The audit archive (chapter 14): a second copy of the audit log and the hash chain's anchors, in a
// container with a time-based immutability policy. No shared keys, no public access, no public
// network. After the first deployment, lock the policy (docs/chapters/ch14.md): a locked policy can
// be extended but never shortened or removed, by anyone.
param location string
param tags object
param name string
param retentionDays int
param writerPrincipalId string
param privateEndpointSubnetId string
param privateDnsZoneId string

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take(name, 24)
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_GZRS' }
  properties: {
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    publicNetworkAccess: 'Disabled'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    defaultToOAuthAuthentication: true
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: account
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 30 }
    containerDeleteRetentionPolicy: { enabled: true, days: 30 }
  }
}

resource archive 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'audit-archive'
  properties: { publicAccess: 'None' }
}

resource immutability 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = {
  parent: archive
  name: 'default'
  properties: {
    immutabilityPeriodSinceCreationInDays: retentionDays
    allowProtectedAppendWrites: false
  }
}

// Storage Blob Data Contributor on this one container. Under the immutability policy it can create
// blobs and read them, but neither overwrite nor delete one.
var blobContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource writer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(archive.id, writerPrincipalId, blobContributor)
  scope: archive
  properties: {
    roleDefinitionId: blobContributor
    principalId: writerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

module endpoint 'private-endpoint.bicep' = {
  name: '${name}-pe'
  params: {
    location: location
    tags: tags
    name: 'pe-${account.name}-blob'
    serviceId: account.id
    groupId: 'blob'
    subnetId: privateEndpointSubnetId
    privateDnsZoneId: privateDnsZoneId
  }
}

output accountName string = account.name
output auditContainerUri string = '${account.properties.primaryEndpoints.blob}${archive.name}'
