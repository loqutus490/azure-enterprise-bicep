param name string
param location string
param appInsightsKey string
param entraClientId string = ''
param enableAuth bool = false
param allowedClientApplications array = []
param vnetSubnetId string = ''
param enableVnetIntegration bool = false
param searchEndpoint string = ''
param searchIndex string = ''
@description('App Service plan SKU name (for example: F1, B1, S1).')
param appServicePlanSkuName string = 'B1'
@description('App Service plan SKU tier (for example: Free, Basic, Standard).')
param appServicePlanSkuTier string = 'Basic'
@description('Use an existing App Service Plan resource ID instead of creating one.')
param existingAppServicePlanResourceId string = ''
@description('Location for the App Service app.')
param appLocation string = location

resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = if (empty(existingAppServicePlanResourceId)) {
  name: 'plan-${name}'
  location: location
  kind: 'linux'
  sku: {
    name: appServicePlanSkuName
    tier: appServicePlanSkuTier
  }
  properties: {
    reserved: true
  }
}

var serverFarmId = empty(existingAppServicePlanResourceId) ? appServicePlan.id : existingAppServicePlanResourceId

resource appService 'Microsoft.Web/sites@2023-01-01' = {
  name: name
  location: appLocation
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: serverFarmId
    virtualNetworkSubnetId: enableVnetIntegration ? vnetSubnetId : null
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: toLower(appServicePlanSkuTier) != 'free'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: appInsightsKey
        }
        {
          name: 'ApplicationInsightsAgent_EXTENSION_VERSION'
          value: '~3'
        }
        {
          name: 'AzureSearch__Endpoint'
          value: searchEndpoint
        }
        {
          name: 'AzureSearch__Index'
          value: searchIndex
        }
      ]
    }
  }
}

// Entra ID (Azure AD) authentication - EasyAuth v2
resource authSettings 'Microsoft.Web/sites/config@2023-01-01' = if (enableAuth) {
  parent: appService
  name: 'authsettingsV2'
  properties: {
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: entraClientId
          openIdIssuer: 'https://sts.windows.net/${subscription().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            'api://${entraClientId}'
          ]
          jwtClaimChecks: {
            allowedClientApplications: allowedClientApplications
          }
        }
      }
    }
    httpSettings: {
      requireHttps: true
    }
    login: {
      tokenStore: {
        enabled: false
      }
    }
  }
}

output appServiceUrl string = 'https://${appService.properties.defaultHostName}'
output appServicePrincipalId string = appService.identity.principalId
output appServiceName string = appService.name
