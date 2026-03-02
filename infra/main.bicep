targetScope = 'resourceGroup'

// =============================================
// Parameters
// =============================================
param environment string
param location string = 'westus3'
param storageLocation string = 'eastus'
param namePrefix string = 'agent13'

@description('Entra ID app registration client ID for authentication. Leave empty to disable auth.')
param entraClientId string = ''

@description('Allowed client application IDs for app-to-app API access. Leave empty to allow any app that has the required API role.')
param allowedClientApplications array = []

@description('Azure AI Search index name used by the API.')
param searchIndexName string = 'legal-index'

@description('Entra ID app registration client ID for the Bot Service. Leave empty to skip bot deployment.')
param botEntraAppId string = ''

@description('Email addresses for budget alert notifications.')
param budgetContactEmails array = []

@description('App Service plan SKU name (for example: F1, B1, S1).')
param appServicePlanSkuName string = 'B1'

@description('App Service plan SKU tier (for example: Free, Basic, Standard).')
param appServicePlanSkuTier string = 'Basic'

@description('Deploy default Azure OpenAI model deployments from IaC.')
param deployOpenAiModels bool = true

@description('Existing App Service Plan resource ID to reuse instead of creating a new plan.')
param existingAppServicePlanResourceId string = ''

@description('Location for the App Service app. Defaults to the deployment location.')
param appLocation string = location

@description('Enable VNet integration and private endpoints for production security.')
param enableNetworking bool = environment == 'prod'

// =============================================
// Networking (VNet, NSGs, Private DNS)
// =============================================
module networking './modules/networking.bicep' = if (enableNetworking) {
  name: 'networking'
  params: {
    name: '${namePrefix}-${environment}'
    location: location
  }
}

// =============================================
// Core Data Platform
// =============================================
module storage './modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: 'st${uniqueString(resourceGroup().id)}'
    location: storageLocation
  }
}

module search './modules/search.bicep' = {
  name: 'search'
  params: {
    name: '${namePrefix}-search-${environment}'
    location: location
    enablePrivateEndpoint: enableNetworking
    privateEndpointSubnetId: enableNetworking ? (networking.?outputs.?peSubnetId ?? '') : ''
    privateDnsZoneId: enableNetworking ? (networking.?outputs.?searchDnsZoneId ?? '') : ''
  }
}

// =============================================
// AI Services
// =============================================
module openai './modules/openai.bicep' = {
  name: 'openai'
  params: {
    name: '${namePrefix}-openai-${environment}'
    location: location
    deployModelDeployments: deployOpenAiModels
    enablePrivateEndpoint: enableNetworking
    privateEndpointSubnetId: enableNetworking ? (networking.?outputs.?peSubnetId ?? '') : ''
    privateDnsZoneId: enableNetworking ? (networking.?outputs.?openaiDnsZoneId ?? '') : ''
  }
}

// =============================================
// Security
// =============================================
module keyvault './modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: '${namePrefix}-kv-${environment}'
    location: location
  }
}

// =============================================
// Observability
// =============================================
module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    name: '${namePrefix}-insights-${environment}'
    location: location
  }
}

// =============================================
// Application (Web Chat Interface)
// =============================================
module app './modules/appservice.bicep' = {
  name: 'app'
  params: {
    name: '${namePrefix}-app-${environment}'
    location: location
    appInsightsKey: monitoring.outputs.instrumentationKey
    entraClientId: entraClientId
    allowedClientApplications: allowedClientApplications
    enableAuth: entraClientId != ''
    vnetSubnetId: enableNetworking ? (networking.?outputs.?appSubnetId ?? '') : ''
    enableVnetIntegration: enableNetworking
    searchEndpoint: search.outputs.searchEndpoint
    searchIndex: searchIndexName
    appServicePlanSkuName: appServicePlanSkuName
    appServicePlanSkuTier: appServicePlanSkuTier
    existingAppServicePlanResourceId: existingAppServicePlanResourceId
    appLocation: appLocation
  }
}

// =============================================
// Bot Service (Teams / Web Chat channel)
// =============================================
module bot './modules/botservice.bicep' = if (botEntraAppId != '') {
  name: 'bot'
  params: {
    name: '${namePrefix}-bot-${environment}'
    location: location
    appServiceUrl: app.outputs.appServiceUrl
    entraAppId: botEntraAppId
  }
}

// =============================================
// Cost Management
// =============================================
module budget './modules/budget.bicep' = {
  name: 'budget'
  params: {
    environment: environment
    amount: environment == 'prod' ? 1000 : 200
    contactEmails: budgetContactEmails
  }
}

// =============================================
// Outputs
// =============================================
output appUrl string = app.outputs.appServiceUrl
output searchService string = search.outputs.searchName
output openaiEndpoint string = openai.outputs.endpoint
output keyVaultName string = keyvault.outputs.keyVaultName
