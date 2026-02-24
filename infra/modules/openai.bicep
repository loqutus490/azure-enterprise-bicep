param name string
param location string

resource openai 'Microsoft.CognitiveServices/accounts@2023-05-01' = {
  name: name
  location: location
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    publicNetworkAccess: 'enabled'
  }
}

//
// Chat Model Deployment
//
resource chatModel 'Microsoft.CognitiveServices/accounts/deployments@2023-05-01' = {
  name: '${name}/gpt-4o'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-05-13'
    }
    scaleSettings: {
      scaleType: 'Standard'
    }
  }
  dependsOn: [
    openai
  ]
}

//
// Embedding Model Deployment
//
resource embeddingModel 'Microsoft.CognitiveServices/accounts/deployments@2023-05-01' = {
  name: '${name}/text-embedding-3-large'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-large'
      version: '1'
    }
    scaleSettings: {
      scaleType: 'Standard'
    }
  }
  dependsOn: [
    openai
  ]
}

output endpoint string = openai.properties.endpoint

