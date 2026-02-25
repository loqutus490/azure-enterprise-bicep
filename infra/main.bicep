targetScope = 'resourceGroup'

param environment string
param location string = 'westus3'
param storageLocation string = 'eastus'
param namePrefix string = 'agent13'

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
  }
}

module openai './modules/openai.bicep' = {
  name: 'openai'
  params: {
    name: '${namePrefix}-openai-${environment}'
    location: location
  }
}

module keyvault './modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: '${namePrefix}-kv-${environment}'
    location: location
  }
}

module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    name: '${namePrefix}-insights-${environment}'
    location: location
  }
}

module app './modules/appservice.bicep' = {
  name: 'app'
  params: {
    name: '${namePrefix}-app-${environment}'
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
