using '../main.bicep'

param environment = 'dev'
param location = 'westus3'
param deployOpenAiModels = false
param deployRoleAssignments = false
param searchIndexName = 'agent13-index'

// Entra ID: set after running scripts/setup-entra.sh
param entraClientId = 'd096fae4-910b-439b-84f1-5bb7966cc705'
param allowedClientApplications = []
// param botEntraAppId = '<your-bot-app-client-id>'

param enableNetworking = false
