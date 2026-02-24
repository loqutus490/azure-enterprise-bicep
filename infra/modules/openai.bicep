//
// Chat Model Deployment
//
resource chatModel 'Microsoft.CognitiveServices/accounts/deployments@2023-05-01' = {
  name: '${name}/gpt-4o'
  sku: {
    name: 'Standard'
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-05-13'
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
  sku: {
    name: 'Standard'
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-large'
      version: '1'
    }
  }
  dependsOn: [
    openai
  ]
}
