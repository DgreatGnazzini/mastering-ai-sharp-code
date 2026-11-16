// One private endpoint, with its DNS record in the matching private zone.
param location string
param tags object
param name string
param serviceId string
param groupId string
param subnetId string
param privateDnsZoneId string

resource endpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    subnet: { id: subnetId }
    privateLinkServiceConnections: [
      {
        name: name
        properties: {
          privateLinkServiceId: serviceId
          groupIds: [groupId]
        }
      }
    ]
  }
}

resource dns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [{ name: groupId, properties: { privateDnsZoneId: privateDnsZoneId } }]
  }
}
