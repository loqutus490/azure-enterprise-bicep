targetScope = 'resourceGroup'

@description('Location for all resources except storage if overridden by the base template.')
param location string = 'westus3'

@description('Resource naming prefix.')
param namePrefix string = 'agent13'

@description('Entra ID app registration client ID for authentication.')
param entraClientId string

@description('Approved caller application IDs for app-to-app API access.')
param allowedClientApplications array

@description('Budget alert recipients.')
param budgetContactEmails array = []

@description('Optional bot Entra app ID. Leave empty to skip bot deployment.')
param botEntraAppId string = ''

@description('Deploy default Azure OpenAI model deployments from IaC.')
param deployOpenAiModels bool = false

@description('Existing App Service Plan resource ID to reuse instead of creating one.')
param existingAppServicePlanResourceId string = ''

@description('App Service location override. Defaults to deployment location.')
param appLocation string = location

@description('App Service plan SKU for hardened production deployment.')
param appServicePlanSkuName string = 'B1'

@description('App Service plan SKU tier for hardened production deployment.')
param appServicePlanSkuTier string = 'Basic'

@description('Azure AI Search index name used by the API.')
param searchIndexName string = 'legal-index'

module hardened './main.bicep' = {
  name: 'hardened-prod'
  params: {
    environment: 'prod'
    location: location
    namePrefix: namePrefix
    entraClientId: entraClientId
    allowedClientApplications: allowedClientApplications
    budgetContactEmails: budgetContactEmails
    botEntraAppId: botEntraAppId
    deployOpenAiModels: deployOpenAiModels
    deployRoleAssignments: true
    debugRagEnabled: false
    enableNetworking: true
    useUniqueNames: true
    existingAppServicePlanResourceId: existingAppServicePlanResourceId
    appLocation: appLocation
    appServicePlanSkuName: appServicePlanSkuName
    appServicePlanSkuTier: appServicePlanSkuTier
    searchIndexName: searchIndexName
  }
}

output appUrl string = hardened.outputs.appUrl
output keyVaultName string = hardened.outputs.keyVaultName
output keyVaultUri string = hardened.outputs.keyVaultUri
output searchService string = hardened.outputs.searchService
