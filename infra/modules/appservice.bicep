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
param openAiEndpoint string = ''
param openAiDeployment string = ''
param openAiEmbeddingDeployment string = ''
param keyVaultName string = ''
@description('App Service plan SKU name (for example: F1, B1, S1).')
param appServicePlanSkuName string = 'B1'
@description('App Service plan SKU tier (for example: Free, Basic, Standard).')
param appServicePlanSkuTier string = 'Basic'
@description('Use an existing App Service Plan resource ID instead of creating one.')
param existingAppServicePlanResourceId string = ''
@description('Location for the App Service app.')
param appLocation string = location
@description('EasyAuth behavior for unauthenticated clients.')
@allowed([
  'Return401'
  'RedirectToLoginPage'
])
param unauthenticatedClientAction string = 'Return401'
@description('ASP.NET Core environment value (Development/Production).')
param aspNetCoreEnvironment string = 'Production'
@description('Required delegated scope for API access.')
param requiredScope string = 'user_impersonation'
@description('Allow bypassing API authorization checks in Development only.')
param bypassAuthInDevelopment bool = false
@description('Allow bypassing matter-level claim checks in Development only.')
param bypassMatterAuthorizationInDevelopment bool = true
@description('Enable protected retrieval diagnostics endpoint.')
param debugRagEnabled bool = false

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
      appCommandLine: 'dotnet LegalRagApp.dll'
      appSettings: [
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
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
        {
          name: 'AzureSearch__UseManagedIdentity'
          value: 'true'
        }
        {
          name: 'AzureOpenAI__Endpoint'
          value: openAiEndpoint
        }
        {
          name: 'AzureOpenAI__Deployment'
          value: openAiDeployment
        }
        {
          name: 'AzureOpenAI__EmbeddingDeployment'
          value: openAiEmbeddingDeployment
        }
        {
          name: 'AzureOpenAI__UseManagedIdentity'
          value: 'true'
        }
        {
          name: 'KeyVault__Name'
          value: keyVaultName
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: aspNetCoreEnvironment
        }
        {
          name: 'AzureAd__Instance'
          value: environment().authentication.loginEndpoint
        }
        {
          name: 'AzureAd__TenantId'
          value: subscription().tenantId
        }
        {
          name: 'AzureAd__ClientId'
          value: entraClientId
        }
        {
          name: 'AzureAd__Audience'
          value: entraClientId
        }
        {
          name: 'Authorization__RequiredScope'
          value: requiredScope
        }
        {
          name: 'Authorization__BypassAuthInDevelopment'
          value: string(bypassAuthInDevelopment)
        }
        {
          name: 'Authorization__BypassMatterAuthorizationInDevelopment'
          value: string(bypassMatterAuthorizationInDevelopment)
        }
        {
          name: 'DebugRag__Enabled'
          value: string(debugRagEnabled)
        }
        {
          name: 'DEBUG_RAG'
          value: string(debugRagEnabled)
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
      unauthenticatedClientAction: unauthenticatedClientAction
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: entraClientId
          openIdIssuer: '${environment().authentication.loginEndpoint}${subscription().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            entraClientId
            'api://${entraClientId}'
          ]
          jwtClaimChecks: length(allowedClientApplications) > 0 ? {
            allowedClientApplications: allowedClientApplications
          } : null
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
