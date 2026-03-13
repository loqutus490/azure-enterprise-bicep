using '../main.bicep'

param environment = 'prod'
param location = 'westus3'
param appServicePlanSkuName = 'B1'
param appServicePlanSkuTier = 'Basic'
param deployOpenAiModels = false
param deployRoleAssignments = true

// =============================================================================
// SECURITY NOTE: Sensitive values must be provided via pipeline variables
// or Azure DevOps/GitHub secrets - NEVER commit real values to source control
// =============================================================================
// Budget alerts: Set BUDGET_ALERT_EMAILS pipeline variable (comma-separated)
// Example in pipeline: BUDGET_ALERT_EMAILS="admin@contoso.com,finops@contoso.com"
param budgetContactEmails = []

// Entra ID: Set ENTRA_CLIENT_ID pipeline variable after running scripts/setup-entra.sh
// In GitHub Actions: Use ${{ vars.ENTRA_CLIENT_ID }} or ${{ secrets.ENTRA_CLIENT_ID }}
// In Azure DevOps: Use $(ENTRA_CLIENT_ID) pipeline variable
param entraClientId = ''

// Allowed caller apps: Set ALLOWED_CLIENT_APPS pipeline variable (JSON array or comma-separated)
// These are the Entra ID app registrations allowed to call this API
param allowedClientApplications = []

// param botEntraAppId = '' // Set via pipeline: BOT_ENTRA_APP_ID

param enableNetworking = true
