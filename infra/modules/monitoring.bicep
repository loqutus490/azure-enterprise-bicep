module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    name: 'appi-${suffix}'
    location: location
  }
}
