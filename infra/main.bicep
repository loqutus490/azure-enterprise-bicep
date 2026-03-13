targetScope = 'resourceGroup'

// =============================================
// Parameters
// =============================================
@description('Deployment environment. Determines resource configurations and security settings.')
@allowed([
  'dev'
  'prod'
])
param environment string

@description('Primary Azure region for resource deployment.')
param location string = 'westus3'

@description('Azure region for the Storage Account. May differ from primary location for compliance or latency reasons.')
param storageLocation string = 'eastus'

@description('Prefix used for naming Azure resources. Should be short and alphanumeric.')
param namePrefix string = 'agent13'

@description('Entra ID app registration client ID for authentication. Leave empty to disable auth.')
param entraClientId string = ''

@description('Allowed client application IDs for app-to-app API access. Leave empty to allow any app that has the required API role.')
param allowedClientApplications array = []

@description('Azure AI Search index name used by the API.')
param searchIndexName string = 'legal-index'

@description('Entra ID app registration client ID for the Bot Service. Leave empty to skip bot deployment.')
param botEntraAppId string = ''

@description('Email addresses for budget alert notifications.')
param budgetContactEmails array = []

@description('App Service plan SKU name (for example: F1, B1, S1).')
param appServicePlanSkuName string = 'B1'

@description('App Service plan SKU tier (for example: Free, Basic, Standard).')
param appServicePlanSkuTier string = 'Basic'

@description('Deploy default Azure OpenAI model deployments from IaC.')
param deployOpenAiModels bool = true

@description('Existing App Service Plan resource ID to reuse instead of creating a new plan.')
param existingAppServicePlanResourceId string = ''

@description('Location for the App Service app. Defaults to the deployment location.')
param appLocation string = location

@description('Enable VNet integration and private endpoints for production security.')
param enableNetworking bool = environment == 'prod'

@description('Deploy RBAC role assignments for the app managed identity (requires roleAssignments/write permissions).')
param deployRoleAssignments bool = true

@description('Enable retrieval diagnostics endpoint (must remain false in production unless explicitly needed).')
param debugRagEnabled bool = false

@description('Append a deterministic unique suffix to globally unique resource names (Search/OpenAI) to avoid naming collisions.')
param useUniqueNames bool = false

// =============================================
// Resource Tagging Parameters
// =============================================
@description('Project or application name for resource tagging.')
param projectName string = 'LegalRagApp'

@description('Cost center code for billing and chargeback.')
param costCenter string = ''

@description('Owner or team responsible for these resources.')
param owner string = ''

@description('Deployment timestamp for tagging. Auto-generated if not specified.')
param deploymentDate string = utcNow('yyyy-MM-dd')

// =============================================
// Common Tags
// =============================================
var commonTags = {
  Environment: environment
  Project: projectName
  CostCenter: costCenter
  Owner: owner
  DeploymentDate: deploymentDate
  ManagedBy: 'Bicep'
}

var uniqueSuffix = toLower(substring(uniqueString(resourceGroup().id, environment), 0, 6))
var searchServiceName = useUniqueNames ? '${namePrefix}-search-${environment}-${uniqueSuffix}' : '${namePrefix}-search-${environment}'
var openAiAccountName = useUniqueNames ? '${namePrefix}-openai-${environment}-${uniqueSuffix}' : '${namePrefix}-openai-${environment}'
var keyVaultName = useUniqueNames ? '${namePrefix}-kv-${environment}-${uniqueSuffix}' : '${namePrefix}-kv-${environment}'
var appServiceName = useUniqueNames ? '${namePrefix}-app-${environment}-${uniqueSuffix}' : '${namePrefix}-app-${environment}'

// =============================================
// Networking (VNet, NSGs, Private DNS)
// =============================================
module networking './modules/networking.bicep' = if (enableNetworking) {
  name: 'networking'
  params: {
    name: '${namePrefix}-${environment}'
    location: location
    tags: commonTags
  }
}

// =============================================
// Core Data Platform
// =============================================
module storage './modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: 'st${uniqueString(resourceGroup().id)}'
    location: storageLocation
    enablePrivateEndpoint: enableNetworking
    privateEndpointSubnetId: enableNetworking ? (networking.?outputs.?peSubnetId ?? '') : ''
    privateDnsZoneId: enableNetworking ? (networking.?outputs.?blobDnsZoneId ?? '') : ''
    tags: commonTags
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
  }
}

module search './modules/search.bicep' = {
  name: 'search'
  params: {
    name: searchServiceName
    location: location
    enablePrivateEndpoint: enableNetworking
    privateEndpointSubnetId: enableNetworking ? (networking.?outputs.?peSubnetId ?? '') : ''
    privateDnsZoneId: enableNetworking ? (networking.?outputs.?searchDnsZoneId ?? '') : ''
    tags: commonTags
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
  }
}

// =============================================
// AI Services
// =============================================
module openai './modules/openai.bicep' = {
  name: 'openai'
  params: {
    name: openAiAccountName
    location: location
    deployModelDeployments: deployOpenAiModels
    enablePrivateEndpoint: enableNetworking
    privateEndpointSubnetId: enableNetworking ? (networking.?outputs.?peSubnetId ?? '') : ''
    privateDnsZoneId: enableNetworking ? (networking.?outputs.?openaiDnsZoneId ?? '') : ''
    tags: commonTags
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
  }
}

// =============================================
// Security
// =============================================
module keyvault './modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: keyVaultName
    location: location
    enablePrivateEndpoint: enableNetworking
    privateEndpointSubnetId: enableNetworking ? (networking.?outputs.?peSubnetId ?? '') : ''
    privateDnsZoneId: enableNetworking ? (networking.?outputs.?kvDnsZoneId ?? '') : ''
    tags: commonTags
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
  }
}

// =============================================
// Observability
// =============================================
module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    name: '${namePrefix}-insights-${environment}'
    location: location
    tags: commonTags
  }
}

// =============================================
// Application (Web Chat Interface)
// =============================================
module app './modules/appservice.bicep' = {
  name: 'app'
  params: {
    name: appServiceName
    location: location
    appInsightsKey: monitoring.outputs.instrumentationKey
    entraClientId: entraClientId
    allowedClientApplications: allowedClientApplications
    enableAuth: entraClientId != ''
    vnetSubnetId: enableNetworking ? (networking.?outputs.?appSubnetId ?? '') : ''
    enableVnetIntegration: enableNetworking
    searchEndpoint: search.outputs.searchEndpoint
    searchIndex: searchIndexName
    openAiEndpoint: openai.outputs.endpoint
    openAiDeployment: openai.outputs.chatDeploymentName
    openAiEmbeddingDeployment: environment == 'dev' ? 'text-embedding-3-small' : openai.outputs.embeddingDeploymentName
    keyVaultName: keyvault.outputs.keyVaultName
    appServicePlanSkuName: appServicePlanSkuName
    appServicePlanSkuTier: appServicePlanSkuTier
    existingAppServicePlanResourceId: existingAppServicePlanResourceId
    appLocation: appLocation
    unauthenticatedClientAction: environment == 'dev' ? 'RedirectToLoginPage' : 'Return401'
    aspNetCoreEnvironment: environment == 'dev' ? 'Development' : 'Production'
    requiredScope: 'access_as_user'
    bypassAuthInDevelopment: false
    bypassMatterAuthorizationInDevelopment: environment == 'dev'
    debugRagEnabled: debugRagEnabled
    tags: commonTags
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
  }
}

resource openaiAccount 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = {
  name: openAiAccountName
}

resource searchService 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: searchServiceName
}

resource keyVaultResource 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
  dependsOn: [keyvault]
}

var openAiUserRoleDefinitionId = '/subscriptions/${subscription().subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
var searchIndexDataReaderRoleDefinitionId = '/subscriptions/${subscription().subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/1407120a-92aa-4202-b7e9-c0e197c71c8f'

var keyVaultSecretsUserRoleDefinitionId = '/subscriptions/${subscription().subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6'
var appResourceName = appServiceName

resource openAiUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(openaiAccount.id, appResourceName, openAiUserRoleDefinitionId)
  scope: openaiAccount
  properties: {
    principalId: app.outputs.appServicePrincipalId
    roleDefinitionId: openAiUserRoleDefinitionId
    principalType: 'ServicePrincipal'
  }
}

resource searchDataReaderRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(searchService.id, appResourceName, searchIndexDataReaderRoleDefinitionId)
  scope: searchService
  properties: {
    principalId: app.outputs.appServicePrincipalId
    roleDefinitionId: searchIndexDataReaderRoleDefinitionId
    principalType: 'ServicePrincipal'
  }
}


resource keyVaultSecretsUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(keyVaultResource.id, appResourceName, keyVaultSecretsUserRoleDefinitionId)
  scope: keyVaultResource
  properties: {
    principalId: app.outputs.appServicePrincipalId
    roleDefinitionId: keyVaultSecretsUserRoleDefinitionId
    principalType: 'ServicePrincipal'
  }
}

// =============================================
// Bot Service (Teams / Web Chat channel)
// =============================================
module bot './modules/botservice.bicep' = if (botEntraAppId != '') {
  name: 'bot'
  params: {
    name: '${namePrefix}-bot-${environment}'
    appServiceUrl: app.outputs.appServiceUrl
    entraAppId: botEntraAppId
    tags: commonTags
  }
}

// =============================================
// Cost Management
// =============================================
module budget './modules/budget.bicep' = {
  name: 'budget'
  params: {
    environment: environment
    amount: environment == 'prod' ? 1000 : 200
    contactEmails: budgetContactEmails
  }
}

// =============================================
// Outputs
// =============================================
// NOTE: Only non-sensitive deployment outputs are exposed here.
// The app URL and service names are needed for deployment automation.
output appUrl string = app.outputs.appServiceUrl
output appServiceName string = app.outputs.appServiceName
output searchService string = search.outputs.searchName

// =============================================================================
// SECURITY: The following outputs have been removed or commented out to prevent
// exposure of sensitive infrastructure details in deployment logs and ARM outputs.
// 
// Removed outputs:
// - keyVaultName: Key Vault names should be retrieved securely via Azure CLI/SDK
// - keyVaultUri: Key Vault URIs can be constructed from the name if needed
// - openaiEndpoint: OpenAI endpoints should be retrieved securely via Azure CLI/SDK
//
// To retrieve these values securely in your pipeline:
//   az keyvault show --name <vault-name> --query properties.vaultUri
//   az cognitiveservices account show --name <account-name> -g <rg> --query properties.endpoint
// =============================================================================
