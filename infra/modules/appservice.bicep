module app './modules/appservice.bicep' = {
  name: 'app'
  params: {
    name: 'api-${suffix}'
    location: location
    appInsightsKey: monitoring.outputs.instrumentationKey
  }
}
