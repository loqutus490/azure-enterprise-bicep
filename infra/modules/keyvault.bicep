module keyvault './modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: 'kv-${suffix}'
    location: location
  }
}
