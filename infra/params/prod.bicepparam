using '../main.bicep'

param environment = 'prod'
param location = 'eastus'

// Entra ID: set after running scripts/setup-entra.sh
param entraClientId = 'd096fae4-910b-439b-84f1-5bb7966cc705'
param allowedClientApplications = [
	'8bf4533e-4bae-409a-a4c1-71e178492e3d'
]
// param botEntraAppId = '<your-bot-app-client-id>'
