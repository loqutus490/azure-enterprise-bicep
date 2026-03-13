@description('Base name for the Application Insights and Log Analytics workspace resources.')
param name string

@description('Azure region for the monitoring resources deployment.')
param location string

@description('Resource tags to apply to all resources.')
param tags object = {}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: 'law-${name}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: name
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

output instrumentationKey string = appInsights.properties.InstrumentationKey
output appInsightsId string = appInsights.id
output logAnalyticsWorkspaceId string = logAnalytics.id
