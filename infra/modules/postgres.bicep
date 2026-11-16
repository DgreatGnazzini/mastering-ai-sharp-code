// Azure Database for PostgreSQL flexible server (chapter 14): the vector index, the audit log, and
// the token ledger. Password authentication is off; the only way in is an Entra ID token. The
// migration identity is the Entra administrator; the API's identity is created as an ordinary role
// by the migration job and granted exactly what it uses (src/PriorAuthBot.Core/Data/DatabaseMigrator.cs).
param location string
param tags object
param name string
param tenantId string
param adminPrincipalId string
param adminPrincipalName string
param zoneRedundant bool
param privateEndpointSubnetId string
param privateDnsZoneId string

var database = 'priorauthbot'

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: zoneRedundant ? 'Standard_D4ds_v5' : 'Standard_B2ms'
    tier: zoneRedundant ? 'GeneralPurpose' : 'Burstable'
  }
  properties: {
    version: '17'
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
      tenantId: tenantId
    }
    network: { publicNetworkAccess: 'Disabled' }
    storage: { storageSizeGB: 64, autoGrow: 'Enabled' }
    backup: {
      // 35 days of point-in-time restore, copied to the paired region for disaster recovery.
      backupRetentionDays: 35
      geoRedundantBackup: 'Enabled'
    }
    highAvailability: { mode: zoneRedundant ? 'ZoneRedundant' : 'Disabled' }
  }
}

// pgvector must be allow-listed before CREATE EXTENSION works.
resource extensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: server
  name: 'azure.extensions'
  properties: { value: 'VECTOR', source: 'user-override' }
}

resource db 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: server
  name: database
  dependsOn: [extensions]
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

resource admin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
  parent: server
  name: adminPrincipalId
  dependsOn: [db]
  properties: {
    principalType: 'ServicePrincipal'
    principalName: adminPrincipalName
    tenantId: tenantId
  }
}

module endpoint 'private-endpoint.bicep' = {
  name: '${name}-pe'
  dependsOn: [admin]
  params: {
    location: location
    tags: tags
    name: 'pe-${name}'
    serviceId: server.id
    groupId: 'postgresqlServer'
    subnetId: privateEndpointSubnetId
    privateDnsZoneId: privateDnsZoneId
  }
}

output fqdn string = server.properties.fullyQualifiedDomainName
output database string = database
