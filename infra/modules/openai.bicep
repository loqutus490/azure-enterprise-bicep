module openai './modules/openai.bicep' = {
  name: 'openai'
  params: {
    name: 'openai-${suffix}'
    location: location
  }
}
