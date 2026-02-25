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

@description('Entra ID app registration client ID for the Bot Service. Leave empty to skip bot deployment.')
param botEntraAppId string = ''

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
    privateEndpointSubnetId: enableNetworking ? networking.outputs.peSubnetId : ''
    privateDnsZoneId: enableNetworking ? networking.outputs.searchDnsZoneId : ''
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
    enableAuth: entraClientId != ''
    vnetSubnetId: enableNetworking ? networking.outputs.appSubnetId : ''
    enableVnetIntegration: enableNetworking
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
  }
}

// =============================================
// Outputs
// =============================================
output appUrl string = app.outputs.appServiceUrl
output searchService string = search.outputs.searchName
output openaiEndpoint string = openai.outputs.endpoint
output keyVaultName string = keyvault.outputs.keyVaultName
