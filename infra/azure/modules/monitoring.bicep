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
