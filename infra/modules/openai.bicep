param name string
param location string
param enablePrivateEndpoint bool = false
param privateEndpointSubnetId string = ''
param privateDnsZoneId string = ''
@description('Deploy default chat and embedding model deployments.')
param deployModelDeployments bool = true

@description('Resource tags to apply to all resources')
param tags object = {}

@description('Log Analytics Workspace ID for diagnostic settings')
param logAnalyticsWorkspaceId string = ''

resource openai 'Microsoft.CognitiveServices/accounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: name
    publicNetworkAccess: enablePrivateEndpoint ? 'disabled' : 'enabled'
    disableLocalAuth: true
  }
}

resource openaiPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = if (enablePrivateEndpoint) {
  name: 'pe-${name}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'plsc-${name}'
        properties: {
          privateLinkServiceId: openai.id
          groupIds: [
            'account'
          ]
        }
      }
    ]
  }
}

resource openaiDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = if (enablePrivateEndpoint) {
  parent: openaiPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

resource chatModel 'Microsoft.CognitiveServices/accounts/deployments@2023-05-01' = if (deployModelDeployments) {
  parent: openai
  name: 'gpt-4o'
  sku: {
    name: 'Standard'
    capacity: 1
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-05-13'
    }
  }
}

resource embeddingModel 'Microsoft.CognitiveServices/accounts/deployments@2023-05-01' = if (deployModelDeployments) {
  parent: openai
  name: 'text-embedding-3-large'
  dependsOn: [chatModel]
  sku: {
    name: 'Standard'
    capacity: 1
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-large'
      version: '1'
    }
  }
}

// =============================================
// Diagnostic Settings
// =============================================
resource openaiDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(logAnalyticsWorkspaceId)) {
  name: 'diag-${name}'
  scope: openai
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        category: 'Audit'
        enabled: true
        retentionPolicy: {
          enabled: false
          days: 0
        }
      }
      {
        category: 'RequestResponse'
        enabled: true
        retentionPolicy: {
          enabled: false
          days: 0
        }
      }
      {
        category: 'Trace'
        enabled: true
        retentionPolicy: {
          enabled: false
          days: 0
        }
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
        retentionPolicy: {
          enabled: false
          days: 0
        }
      }
    ]
  }
}

output endpoint string = openai.properties.endpoint
output chatDeploymentName string = deployModelDeployments ? chatModel.name : 'gpt-4o'
output embeddingDeploymentName string = deployModelDeployments ? embeddingModel.name : 'text-embedding-3-large'
