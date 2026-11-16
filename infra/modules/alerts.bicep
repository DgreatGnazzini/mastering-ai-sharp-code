// Alerts (chapter 14) on the log events chapters 13 and 14 write: the daily spend alert (EventId 1302)
// and a broken audit chain found by the archive (EventId 1311). Both page the same action group.
param location string
param tags object
param prefix string
param email string
param appInsightsId string

resource group 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-${prefix}'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'priorauth'
    enabled: true
    emailReceivers: [{ name: 'on-call', emailAddress: email, useCommonAlertSchema: true }]
  }
}

var rules = [
  {
    name: 'spend'
    description: 'PriorAuthBot model spend crossed a daily alert threshold (chapter 13).'
    severity: 2
    query: 'AppTraces | where tostring(Properties.EventId) == "1302"'
  }
  {
    name: 'audit-chain'
    description: 'The audit archive found records that do not continue the hash chain (chapter 14). Tell compliance.'
    severity: 0
    query: 'AppTraces | where tostring(Properties.EventId) == "1311"'
  }
  {
    name: 'guardrail-failures'
    description: 'More than 20 requests in 15 minutes failed on the guardrails or the audit log (chapters 12 and 13).'
    severity: 2
    query: 'AppRequests | where ResultCode in ("422", "503") | summarize failures = count() | where failures > 20'
  }
]

resource alerts 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for rule in rules: {
  name: 'alert-${prefix}-${rule.name}'
  location: location
  tags: tags
  properties: {
    description: rule.description
    severity: rule.severity
    enabled: true
    scopes: [appInsightsId]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: rule.query
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: [group.id] }
  }
}]
