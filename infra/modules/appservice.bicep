param name string
param location string
param appInsightsKey string

resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: 'plan-${name}'
  location: location
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
}

resource appService 'Microsoft.Web/sites@2023-01-01' = {
  name: name
  location: location
  properties: {
    serverFarmId: appServicePlan.id
    siteConfig: {
      appSettings: [
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: appInsightsKey
        }
        {
          name: 'ApplicationInsightsAgent_EXTENSION_VERSION'
          value: '~3'
        }
      ]
    }
  }
}

output appServiceUrl string = 'https://${appService.properties.defaultHostName}'
