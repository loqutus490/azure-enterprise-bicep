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
