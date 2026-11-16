// The virtual network (chapter 14): one subnet for the Container Apps environment, one for private
// endpoints, and a private DNS zone for each service, so that inside the network
// oai-....openai.azure.com resolves to a private address and traffic never leaves it.
param location string
param tags object
param name string
param aiSearch bool

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: ['10.40.0.0/16'] }
    subnets: [
      {
        // Container Apps with workload profiles: a delegated subnet, /27 at least; /23 leaves room to scale.
        name: 'snet-apps'
        properties: {
          addressPrefix: '10.40.0.0/23'
          delegations: [{ name: 'apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: '10.40.2.0/24'
          privateEndpointNetworkPolicies: 'Enabled'
        }
      }
    ]
  }
}

var zoneNames = union({
  openai: 'privatelink.openai.azure.com'
  postgres: 'privatelink.postgres.database.azure.com'
  blob: 'privatelink.blob.${environment().suffixes.storage}'
  vault: 'privatelink.vaultcore.azure.net'
}, aiSearch ? { search: 'privatelink.search.windows.net' } : {})

resource zones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in items(zoneNames): {
  name: zone.value
  location: 'global'
  tags: tags
}]

resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in items(zoneNames): {
  parent: zones[i]
  name: '${name}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: vnet.id }
  }
}]

output vnetId string = vnet.id
output appsSubnetId string = vnet.properties.subnets[0].id
output privateEndpointSubnetId string = vnet.properties.subnets[1].id
output zones object = toObject(items(zoneNames), z => z.key, z => resourceId('Microsoft.Network/privateDnsZones', z.value))
