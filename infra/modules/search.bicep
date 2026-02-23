module search './modules/search.bicep' = {
  name: 'search'
  params: {
    name: 'srch-${suffix}'
    location: location
  }
}
