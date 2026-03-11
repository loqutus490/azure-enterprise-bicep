using '../main.bicep'

param environment = 'prod'
param location = 'westus3'
param appServicePlanSkuName = 'B1'
param appServicePlanSkuTier = 'Basic'
param deployOpenAiModels = false
param deployRoleAssignments = true
param budgetContactEmails = [
  '<set-budget-alert-email>'
]

// Entra ID: set after running scripts/setup-entra.sh
param entraClientId = '<set-in-pipeline-or-local-secrets>'
param allowedClientApplications = [
  '<set-approved-caller-app-id>'
]
// param botEntraAppId = '<your-bot-app-client-id>'

param enableNetworking = true
