param name string
param location string
param appServiceUrl string
param entraAppId string

resource bot 'Microsoft.BotService/botServices@2022-09-15' = {
  name: name
  location: 'global'
  kind: 'azurebot'
  sku: {
    name: 'S1'
  }
  properties: {
    displayName: 'Legal AI Assistant'
    description: 'Secure AI assistant for law firm internal use'
    endpoint: '${appServiceUrl}/api/messages'
    msaAppId: entraAppId
    msaAppType: 'SingleTenant'
    msaAppTenantId: subscription().tenantId
    schemaTransformationVersion: '1.3'
  }
}

// Microsoft Teams channel
resource teamsChannel 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  parent: bot
  name: 'MsTeamsChannel'
  location: 'global'
  properties: {
    channelName: 'MsTeamsChannel'
    properties: {
      isEnabled: true
    }
  }
}

// Web Chat channel (enabled by default but explicitly configured)
resource webChatChannel 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  parent: bot
  name: 'WebChatChannel'
  location: 'global'
  properties: {
    channelName: 'WebChatChannel'
    properties: {
      sites: [
        {
          siteName: 'Default'
          isEnabled: true
        }
      ]
    }
  }
}

output botName string = bot.name
output botEndpoint string = bot.properties.endpoint
