// Log Analytics + workspace-based Application Insights (INFRASTRUCTURE §6.2).

param location string
param namePrefix string
param tags object

@description('Daily ingestion cap in GB (Stage 1: 0.15).')
param dailyQuotaGb string = '0.15'

@description('Retention in days (Stage 1: 30).')
param retentionInDays int = 30

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionInDays
    workspaceCapping: { dailyQuotaGb: json(dailyQuotaGb) }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    DisableLocalAuth: false
  }
}

output workspaceName string = workspace.name
output workspaceCustomerId string = workspace.properties.customerId
output appInsightsConnectionString string = appInsights.properties.ConnectionString

// ---------------------------------------------------------------------------------------------------------------------
// Business and operational alerts (NX-OT-07). The Host exports the "CoreIns.Business" meter through Azure Monitor, so the
// instruments land in the workspace table AppMetrics. Each rule's query returns only violating rows (Count > 0 fires).
// ---------------------------------------------------------------------------------------------------------------------

@description('Create the alert rules.')
param alertsEnabled bool = true

@description('Resource id of an action group to notify; empty creates the rules without notifications (visible in the portal only).')
param alertActionGroupId string = ''

@description('Minutes a sweeper may stay silent before alerting (the slowest sweeper runs every minute).')
param sweeperSilentMinutes int = 15

@description('Outbox lag (seconds, oldest message of a claimed batch) that alerts.')
param outboxLagAlertSeconds int = 300

var alertRules = [
  {
    key: 'sweeper-silent'
    title: 'A sweeper stopped beating'
    severity: 2
    query: 'AppMetrics | where Name == "coreins.sweeper.last_attempt" | summarize arg_max(TimeGenerated, Max) by sweeper = tostring(Properties.sweeper) | where unixtime_seconds_todatetime(Max) < ago(${sweeperSilentMinutes}m)'
  }
  {
    key: 'sweeper-failing'
    title: 'A sweeper pass keeps failing'
    severity: 2
    query: 'AppMetrics | where Name == "coreins.sweeper.runs" and tostring(Properties.outcome) == "failed" | summarize failures = sum(Sum) by sweeper = tostring(Properties.sweeper) | where failures >= 3'
  }
  {
    key: 'outbox-dead-letters'
    title: 'Outbox messages were dead-lettered'
    severity: 2
    query: 'AppMetrics | where Name == "coreins.outbox.dead_letters" | summarize parked = sum(Sum) | where parked > 0'
  }
  {
    key: 'outbox-lag'
    title: 'Outbox lag is high'
    severity: 3
    query: 'AppMetrics | where Name == "coreins.outbox.lag" | summarize worst = max(Max) | where worst > ${outboxLagAlertSeconds}'
  }
  {
    key: 'idempotency-conflicts'
    title: 'Many Idempotency-Key conflicts'
    severity: 3
    query: 'AppMetrics | where Name == "coreins.idempotency.conflicts" | summarize conflicts = sum(Sum) | where conflicts >= 20'
  }
  {
    key: 'payment-failures'
    title: 'Payment failures are spiking'
    severity: 2
    query: 'AppMetrics | where Name == "coreins.payment.failures" | summarize failures = sum(Sum) | where failures >= 5'
  }
]

resource alerts 'Microsoft.Insights/scheduledQueryRules@2022-06-15' = [
  for rule in alertRules: if (alertsEnabled) {
    name: 'alert-${namePrefix}-${rule.key}'
    location: location
    tags: tags
    properties: {
      displayName: rule.title
      description: rule.title
      enabled: true
      severity: rule.severity
      scopes: [workspace.id]
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
      autoMitigate: true
      actions: { actionGroups: empty(alertActionGroupId) ? [] : [alertActionGroupId] }
    }
  }
]
