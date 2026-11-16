// The private DNS zone for an internal Container Apps environment (chapter 14): every app's name in
// the environment's domain resolves to its internal load balancer, inside the virtual network only.
param tags object
param domain string
param staticIp string
param vnetId string

resource zone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: domain
  location: 'global'
  tags: tags
}

resource wildcard 'Microsoft.Network/privateDnsZones/A@2020-06-01' = {
  parent: zone
  name: '*'
  properties: {
    ttl: 300
    aRecords: [{ ipv4Address: staticIp }]
  }
}

resource link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: zone
  name: 'apps-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: vnetId }
  }
}
