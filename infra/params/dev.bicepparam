using '../main.bicep'

param environment = 'dev'
param location = 'westus3'
param deployOpenAiModels = false
param deployRoleAssignments = false
param searchIndexName = 'agent13-index'

// Entra ID: set after running scripts/setup-entra.sh
param entraClientId = '<set-in-pipeline-or-local-secrets>'
param allowedClientApplications = []
// param botEntraAppId = '<your-bot-app-client-id>'

param enableNetworking = false
