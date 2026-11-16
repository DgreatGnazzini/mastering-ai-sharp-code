// Three user-assigned managed identities (chapter 14), each with only the rights its job needs:
//   api      the API: calls the model, reads and writes the index, appends to the audit log.
//   migrate  the migration job: the PostgreSQL Entra administrator. It owns the schema; the API does not.
//   eval     the eval job: calls the model deployments, and nothing else.
// The deploy pipeline's own identity is created once, outside this template (infra/bootstrap).
param location string
param tags object
param prefix string

resource api 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}-api'
  location: location
  tags: tags
}

resource migrate 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}-migrate'
  location: location
  tags: tags
}

resource eval 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}-eval'
  location: location
  tags: tags
}

output apiPrincipalId string = api.properties.principalId
output apiClientId string = api.properties.clientId
output migratePrincipalId string = migrate.properties.principalId
output migrateName string = migrate.name
output evalPrincipalId string = eval.properties.principalId
output all object = {
  api: { id: api.id, name: api.name, clientId: api.properties.clientId, principalId: api.properties.principalId }
  migrate: { id: migrate.id, name: migrate.name, clientId: migrate.properties.clientId, principalId: migrate.properties.principalId }
  eval: { id: eval.id, name: eval.name, clientId: eval.properties.clientId, principalId: eval.properties.principalId }
}
