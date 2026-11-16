// PriorAuthBot's app registration in Microsoft Entra ID (chapter 14), as code, with the Microsoft
// Graph Bicep extension. Deploy it once per environment, as someone who may create applications:
//
//   az deployment group create -g rg-priorauthbot-<env> -f infra/entra/app-registration.bicep -p environmentName=<env>
//
// then pass its appId output to main.bicep as apiClientId. After main.bicep has created the API's
// managed identity, deploy this file again with apiManagedIdentityPrincipalId set: that adds the
// federated credential the on-behalf-of flow uses (chapter 10), so the app has no client secret.
extension microsoftGraphV1

@description('Same as main.bicep.')
param environmentName string

@description('Principal (object) ID of the API\'s managed identity. Empty on the first run.')
param apiManagedIdentityPrincipalId string = ''

@description('Entra ID groups whose members may use PriorAuthBot.')
param userGroupIds array = []

@description('Entra ID groups whose members may read the audit log (chapter 13): compliance only.')
param auditReaderGroupIds array = []

var userRoleId = guid('priorauthbot', environmentName, 'PriorAuthBot.User')
var auditReaderRoleId = guid('priorauthbot', environmentName, 'PriorAuthBot.AuditReader')

resource app 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'priorauthbot-${environmentName}'
  displayName: 'PriorAuthBot (${environmentName})'
  signInAudience: 'AzureADMyOrg'
  identifierUris: ['api://priorauthbot-${environmentName}']

  // Only the groups assigned to this app go into the token, not every group the user is in. A nurse in
  // 300 groups gets a token with the two that matter, and chapter 7's group overage case stays rare.
  groupMembershipClaims: 'ApplicationGroup'

  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: guid('priorauthbot', environmentName, 'PriorAuthBot.Ask')
        value: 'PriorAuthBot.Ask'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'Ask PriorAuthBot'
        adminConsentDescription: 'Ask PriorAuthBot questions as the signed-in user, with that user\'s document permissions.'
      }
      {
        id: guid('priorauthbot', environmentName, 'PriorAuthBot.Search')
        value: 'PriorAuthBot.Search'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'Search policies through PriorAuthBot'
        adminConsentDescription: 'Search the policies the signed-in user may read, through PriorAuthBot\'s MCP server (chapter 10).'
      }
    ]
  }

  appRoles: [
    {
      id: userRoleId
      value: 'PriorAuthBot.User'
      displayName: 'PriorAuthBot user'
      description: 'May use PriorAuthBot. Every endpoint requires this role.'
      allowedMemberTypes: ['User']
      isEnabled: true
    }
    {
      id: auditReaderRoleId
      value: 'PriorAuthBot.AuditReader'
      displayName: 'PriorAuthBot audit reader'
      description: 'May read PriorAuthBot\'s audit log (chapter 13).'
      allowedMemberTypes: ['User']
      isEnabled: true
    }
  ]

  // Chapter 10's on-behalf-of flow: the app proves its identity with a token from the API's managed
  // identity instead of a client secret.
  resource managedIdentityCredential 'federatedIdentityCredentials@v1.0' = if (!empty(apiManagedIdentityPrincipalId)) {
    name: '${app.uniqueName}/api-managed-identity'
    description: 'The PriorAuthBot API\'s user-assigned managed identity'
    audiences: ['api://AzureADTokenExchange']
    issuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
    subject: apiManagedIdentityPrincipalId
  }
}

// Assignment required: someone who is not assigned cannot even get a token for PriorAuthBot.
resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: app.appId
  appRoleAssignmentRequired: true
}

resource users 'Microsoft.Graph/appRoleAssignedTo@v1.0' = [for groupId in userGroupIds: {
  appRoleId: userRoleId
  principalId: groupId
  resourceId: servicePrincipal.id
}]

resource auditReaders 'Microsoft.Graph/appRoleAssignedTo@v1.0' = [for groupId in auditReaderGroupIds: {
  appRoleId: auditReaderRoleId
  principalId: groupId
  resourceId: servicePrincipal.id
}]

output appId string = app.appId
output identifierUri string = app.identifierUris[0]
