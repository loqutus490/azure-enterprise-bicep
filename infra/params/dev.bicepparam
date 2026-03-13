using '../main.bicep'

param environment = 'dev'
param location = 'westus3'
param deployOpenAiModels = false
param deployRoleAssignments = false
param searchIndexName = 'agent13-index'

// =============================================================================
// SECURITY NOTE: Sensitive values must be provided via pipeline variables
// or Azure DevOps/GitHub secrets - NEVER commit real values to source control
// =============================================================================
// Entra ID: Set ENTRA_CLIENT_ID pipeline variable after running scripts/setup-entra.sh
// In GitHub Actions: Use ${{ vars.ENTRA_CLIENT_ID }} or ${{ secrets.ENTRA_CLIENT_ID }}
// In Azure DevOps: Use $(ENTRA_CLIENT_ID) pipeline variable
param entraClientId = ''

param allowedClientApplications = []
// param botEntraAppId = '' // Set via pipeline: BOT_ENTRA_APP_ID

param enableNetworking = false
