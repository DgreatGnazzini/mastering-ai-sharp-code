// Log Analytics and Application Insights (chapter 14). Local authentication is off, so the connection
// string identifies the resource but cannot send anything on its own: the API's managed identity
// needs the Monitoring Metrics Publisher role, which containerapps.bicep grants.
param location string
param tags object
param workspaceName string
param appInsightsName string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    // Operational telemetry, not the audit log: the audit log is in PostgreSQL and the immutable archive.
    retentionInDays: 90
    features: { disableLocalAuth: true }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
    IngestionMode: 'LogAnalytics'
  }
}

output workspaceId string = workspace.id
output workspaceGuid string = workspace.properties.customerId
output appInsightsId string = appInsights.id
output appInsightsName string = appInsights.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
