#!/bin/bash
# =============================================================================
# tenant-deploy.sh — Multi-tenant deployment for azure-enterprise-bicep
#
# Usage:
#   ./tenant-deploy.sh [OPTIONS]
#
# Options:
#   -t, --tenant-id      <id>      Azure Tenant ID (required)
#   -s, --subscription   <id>      Azure Subscription ID (required)
#   -e, --environment    <name>    dev | prod  (default: dev)
#   -l, --location       <region>  Azure region  (default: eastus)
#   -p, --prefix         <name>    Resource name prefix (default: legalrag)
#   -g, --resource-group <name>    Resource group name (default: rg-<prefix>-<env>)
#   -n, --no-app-deploy            Skip .NET build/deploy step
#   -h, --help                     Show this help
#
# Examples:
#   # Deploy dev to a new tenant, interactive login
#   ./tenant-deploy.sh -t 00000000-... -s 11111111-...
#
#   # Deploy prod with a custom prefix and location
#   ./tenant-deploy.sh -t 00000000-... -s 11111111-... -e prod -l westeurope -p myapp
#
#   # Infrastructure only (skip dotnet build)
#   ./tenant-deploy.sh -t 00000000-... -s 11111111-... -n
# =============================================================================

set -euo pipefail

# -----------------------------------------------------------------------
# Defaults
# -----------------------------------------------------------------------
TENANT_ID=""
SUBSCRIPTION_ID=""
ENVIRONMENT="dev"
LOCATION="eastus"
NAME_PREFIX="legalrag"
RESOURCE_GROUP=""
SKIP_APP_DEPLOY=false
APP_PROJECT_PATH="./src/LegalRagApp.csproj"

# -----------------------------------------------------------------------
# Argument parsing
# -----------------------------------------------------------------------
usage() {
  sed -n '/^# Usage:/,/^# ===/p' "$0" | grep -v "^# ===" | sed 's/^# \?//'
  exit 0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -t|--tenant-id)       TENANT_ID="$2";       shift 2 ;;
    -s|--subscription)    SUBSCRIPTION_ID="$2"; shift 2 ;;
    -e|--environment)     ENVIRONMENT="$2";     shift 2 ;;
    -l|--location)        LOCATION="$2";        shift 2 ;;
    -p|--prefix)          NAME_PREFIX="$2";     shift 2 ;;
    -g|--resource-group)  RESOURCE_GROUP="$2";  shift 2 ;;
    -n|--no-app-deploy)   SKIP_APP_DEPLOY=true; shift   ;;
    -h|--help)            usage ;;
    *) echo "Unknown option: $1"; usage ;;
  esac
done

# -----------------------------------------------------------------------
# Validate required args
# -----------------------------------------------------------------------
if [[ -z "$TENANT_ID" || -z "$SUBSCRIPTION_ID" ]]; then
  echo ""
  echo "ERROR: --tenant-id and --subscription are required."
  echo ""
  usage
fi

if [[ "$ENVIRONMENT" != "dev" && "$ENVIRONMENT" != "prod" ]]; then
  echo "ERROR: --environment must be 'dev' or 'prod' (got: $ENVIRONMENT)"
  exit 1
fi

# Derive resource group name if not provided
if [[ -z "$RESOURCE_GROUP" ]]; then
  RESOURCE_GROUP="rg-${NAME_PREFIX}-${ENVIRONMENT}"
fi

PARAMS_FILE="infra/params/${ENVIRONMENT}.bicepparam"

# -----------------------------------------------------------------------
# Banner
# -----------------------------------------------------------------------
echo ""
echo "============================================================"
echo "  azure-enterprise-bicep — Multi-Tenant Deployment"
echo "============================================================"
echo "  Tenant ID      : $TENANT_ID"
echo "  Subscription   : $SUBSCRIPTION_ID"
echo "  Environment    : $ENVIRONMENT"
echo "  Location       : $LOCATION"
echo "  Resource Group : $RESOURCE_GROUP"
echo "  Name Prefix    : $NAME_PREFIX"
echo "  Params file    : $PARAMS_FILE"
echo "  Skip app deploy: $SKIP_APP_DEPLOY"
echo "============================================================"
echo ""

# -----------------------------------------------------------------------
# Prerequisite checks
# -----------------------------------------------------------------------
echo ">> Checking prerequisites..."

command -v az   >/dev/null 2>&1 || { echo "ERROR: Azure CLI (az) not found."; exit 1; }
command -v zip  >/dev/null 2>&1 || { echo "ERROR: zip not found."; exit 1; }
if [[ "$SKIP_APP_DEPLOY" == "false" ]]; then
  command -v dotnet >/dev/null 2>&1 || { echo "ERROR: dotnet CLI not found (use -n to skip app deploy)."; exit 1; }
fi

if [[ ! -f "$PARAMS_FILE" ]]; then
  echo "ERROR: Params file not found: $PARAMS_FILE"
  exit 1
fi

# -----------------------------------------------------------------------
# Azure login / tenant switch
# -----------------------------------------------------------------------
echo ">> Logging in to Azure (tenant: $TENANT_ID)..."

# Check if already authenticated to the right tenant
CURRENT_TENANT=$(az account show --query tenantId -o tsv 2>/dev/null || true)

if [[ "$CURRENT_TENANT" != "$TENANT_ID" ]]; then
  echo "   Current tenant ($CURRENT_TENANT) differs — initiating login..."
  az login --tenant "$TENANT_ID" --output none
else
  echo "   Already authenticated to tenant $TENANT_ID."
fi

echo ">> Setting active subscription: $SUBSCRIPTION_ID"
az account set --subscription "$SUBSCRIPTION_ID"

# Confirm context
ACTIVE_SUB=$(az account show --query "[name, id]" -o tsv)
echo "   Active subscription: $ACTIVE_SUB"

# -----------------------------------------------------------------------
# Resource group
# -----------------------------------------------------------------------
echo ""
echo ">> Ensuring resource group '$RESOURCE_GROUP' exists in $LOCATION..."
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --output none
echo "   Resource group ready."

# -----------------------------------------------------------------------
# Bicep infrastructure deployment
# -----------------------------------------------------------------------
echo ""
echo ">> Deploying Bicep infrastructure..."
echo "   Template : infra/main.bicep"
echo "   Params   : $PARAMS_FILE"
echo "   Overrides: environment=$ENVIRONMENT location=$LOCATION namePrefix=$NAME_PREFIX"

DEPLOY_NAME="deploy-${ENVIRONMENT}-$(date +%Y%m%d%H%M%S)"

az deployment group create \
  --name "$DEPLOY_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "infra/main.bicep" \
  --parameters "$PARAMS_FILE" \
  --parameters environment="$ENVIRONMENT" location="$LOCATION" namePrefix="$NAME_PREFIX" \
  --output table

echo "   Infrastructure deployment complete."

# -----------------------------------------------------------------------
# Resolve deployed resource names
# -----------------------------------------------------------------------
echo ""
echo ">> Resolving deployed resource names..."

WEBAPP_NAME=$(az webapp list \
  --resource-group "$RESOURCE_GROUP" \
  --query "[0].name" -o tsv)

SEARCH_SERVICE=$(az search service list \
  --resource-group "$RESOURCE_GROUP" \
  --query "[0].name" -o tsv)

OPENAI_RESOURCE=$(az cognitiveservices account list \
  --resource-group "$RESOURCE_GROUP" \
  --query "[?kind=='OpenAI'].name | [0]" -o tsv)

OPENAI_DEPLOYMENT=$(az cognitiveservices account deployment list \
  --name "$OPENAI_RESOURCE" \
  --resource-group "$RESOURCE_GROUP" \
  --query "[0].name" -o tsv 2>/dev/null || echo "gpt-4o")

echo "   Web App        : $WEBAPP_NAME"
echo "   Search Service : $SEARCH_SERVICE"
echo "   OpenAI Resource: $OPENAI_RESOURCE"
echo "   OpenAI Deploy  : $OPENAI_DEPLOYMENT"

# -----------------------------------------------------------------------
# Fetch service keys
# -----------------------------------------------------------------------
echo ""
echo ">> Fetching service keys..."

SEARCH_KEY=$(az search admin-key show \
  --service-name "$SEARCH_SERVICE" \
  --resource-group "$RESOURCE_GROUP" \
  --query primaryKey -o tsv)

OPENAI_KEY=$(az cognitiveservices account keys list \
  --name "$OPENAI_RESOURCE" \
  --resource-group "$RESOURCE_GROUP" \
  --query key1 -o tsv)

echo "   Keys retrieved."

# -----------------------------------------------------------------------
# Configure App Service settings
# -----------------------------------------------------------------------
echo ""
echo ">> Configuring App Service settings..."

az webapp config appsettings set \
  --name "$WEBAPP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --settings \
    ASPNETCORE_ENVIRONMENT="$ENVIRONMENT" \
    AzureSearch__Endpoint="https://${SEARCH_SERVICE}.search.windows.net" \
    AzureSearch__Key="$SEARCH_KEY" \
    AzureSearch__Index="legal-index" \
    AzureOpenAI__Endpoint="https://${OPENAI_RESOURCE}.openai.azure.com/" \
    AzureOpenAI__Key="$OPENAI_KEY" \
    AzureOpenAI__Deployment="$OPENAI_DEPLOYMENT" \
  --output none

echo "   App settings configured."

# -----------------------------------------------------------------------
# Build + deploy .NET app (optional)
# -----------------------------------------------------------------------
if [[ "$SKIP_APP_DEPLOY" == "true" ]]; then
  echo ""
  echo ">> Skipping .NET build/deploy (--no-app-deploy set)."
else
  echo ""
  echo ">> Building .NET 8 application..."
  rm -rf publish publish.zip

  dotnet publish "$APP_PROJECT_PATH" \
    -c Release \
    -o ./publish \
    --nologo

  echo ">> Packaging..."
  (cd publish && zip -r ../publish.zip . -q)

  echo ">> Deploying to App Service..."
  az webapp deploy \
    --resource-group "$RESOURCE_GROUP" \
    --name "$WEBAPP_NAME" \
    --src-path ./publish.zip \
    --type zip \
    --output none

  echo ">> Restarting App Service..."
  az webapp restart \
    --resource-group "$RESOURCE_GROUP" \
    --name "$WEBAPP_NAME" \
    --output none

  echo "   Application deployed and restarted."
fi

# -----------------------------------------------------------------------
# Summary
# -----------------------------------------------------------------------
echo ""
echo "============================================================"
echo "  DEPLOYMENT COMPLETE"
echo "============================================================"
echo "  Tenant         : $TENANT_ID"
echo "  Subscription   : $SUBSCRIPTION_ID"
echo "  Resource Group : $RESOURCE_GROUP"
echo "  Environment    : $ENVIRONMENT"
if [[ -n "$WEBAPP_NAME" ]]; then
  echo "  App URL        : https://${WEBAPP_NAME}.azurewebsites.net"
fi
echo "============================================================"
