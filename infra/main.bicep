targetScope = 'resourceGroup'

param environment string
param location string = 'eastus'
param namePrefix string = 'legalrag'

var suffix = '${namePrefix}-${environment}'

module storage './modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: 'st${uniqueString(resourceGroup().id)}'
    location: location
  }
}

module search './modules/search.bicep' = {
  name: 'search'
  params: {
    name: 'srch-${suffix}'
    location: location
  }
}

module openai './modules/openai.bicep' = {
  name: 'openai'
  params: {
    name: 'openai-${suffix}'
    location: location
  }
}

module keyvault './modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: 'kv-${suffix}'
    location: location
  }
}

module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    name: 'appi-${suffix}'
    location: location
  }
}

module app './modules/appservice.bicep' = {
  name: 'app'
  params: {
    name: 'api-${suffix}'
    location: location
    appInsightsKey: monitoring.outputs.instrumentationKey
  }
}

module budget './modules/budget.bicep' = {
  name: 'budget'
  params: {
    environment: environment
    amount: environment == 'prod' ? 1000 : 200
  }
}
