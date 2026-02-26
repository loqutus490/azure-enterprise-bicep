#!/bin/bash

set -euo pipefail

# Resolve repo root so the script works from any directory
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

# ============================================================
# full-deploy.sh — Build, configure, and deploy in one step
#
# Auto-discovers resource names from the resource group so
# nothing is hardcoded.
#
# Usage:
#   RESOURCE_GROUP=my-rg ./scripts/full-deploy.sh
# ============================================================

RESOURCE_GROUP="${RESOURCE_GROUP:-agent13-dev-rg}"

echo "=========================================="
echo "FULL RAG DEPLOYMENT SCRIPT"
echo "=========================================="
echo "Resource Group: $RESOURCE_GROUP"
echo ""

# Check prerequisites
command -v az     >/dev/null 2>&1 || { echo "Error: Azure CLI (az) not found."; exit 1; }
command -v dotnet >/dev/null 2>&1 || { echo "Error: dotnet CLI not found."; exit 1; }
command -v zip    >/dev/null 2>&1 || { echo "Error: zip not found."; exit 1; }

# -------------------------------
# AUTO-DISCOVER RESOURCES
# -------------------------------

echo "Discovering resources in '$RESOURCE_GROUP'..."

WEBAPP_NAME=$(az webapp list \
  --resource-group "$RESOURCE_GROUP" \
  --query "[0].name" -o tsv 2>/dev/null)

if [ -z "$WEBAPP_NAME" ]; then
  echo "Error: No Web App found in resource group '$RESOURCE_GROUP'."
  exit 1
fi

SEARCH_SERVICE=$(az search service list \
  --resource-group "$RESOURCE_GROUP" \
  --query "[0].name" -o tsv 2>/dev/null)

if [ -z "$SEARCH_SERVICE" ]; then
  echo "Error: No Azure Search service found in resource group '$RESOURCE_GROUP'."
  exit 1
fi

OPENAI_RESOURCE=$(az cognitiveservices account list \
  --resource-group "$RESOURCE_GROUP" \
  --query "[?kind=='OpenAI'].name | [0]" -o tsv 2>/dev/null)

if [ -z "$OPENAI_RESOURCE" ]; then
  echo "Error: No Azure OpenAI resource found in resource group '$RESOURCE_GROUP'."
  exit 1
fi

OPENAI_DEPLOYMENT=$(az cognitiveservices account deployment list \
  --name "$OPENAI_RESOURCE" \
  --resource-group "$RESOURCE_GROUP" \
  --query "[?contains(name,'gpt')].name | [0]" -o tsv 2>/dev/null || echo "gpt-4o")

echo "  Web App:           $WEBAPP_NAME"
echo "  Search Service:    $SEARCH_SERVICE"
echo "  OpenAI Resource:   $OPENAI_RESOURCE"
echo "  OpenAI Deployment: $OPENAI_DEPLOYMENT"
echo ""

# -------------------------------
# VERIFY RESOURCES EXIST
# -------------------------------

echo "Verifying Azure Search..."
az search service show \
  --name "$SEARCH_SERVICE" \
  --resource-group "$RESOURCE_GROUP" \
  > /dev/null

echo "Verifying Azure OpenAI..."
az cognitiveservices account show \
  --name "$OPENAI_RESOURCE" \
  --resource-group "$RESOURCE_GROUP" \
  > /dev/null

# -------------------------------
# FETCH KEYS AUTOMATICALLY
# -------------------------------

echo "Fetching Azure Search Admin Key..."
SEARCH_KEY=$(az search admin-key show \
  --service-name "$SEARCH_SERVICE" \
  --resource-group "$RESOURCE_GROUP" \
  --query primaryKey -o tsv)

echo "Fetching Azure OpenAI Key..."
OPENAI_KEY=$(az cognitiveservices account keys list \
  --name "$OPENAI_RESOURCE" \
  --resource-group "$RESOURCE_GROUP" \
  --query key1 -o tsv)

# -------------------------------
# BUILD + PUBLISH
# -------------------------------

echo "Cleaning previous builds..."
rm -rf src/bin src/obj publish publish.zip

echo "Publishing .NET 8 app..."
dotnet publish src -c Release -o publish

# -------------------------------
# ZIP PACKAGE
# -------------------------------

echo "Packaging..."
(cd publish && zip -r ../publish.zip . -q)

# -------------------------------
# SET APP SETTINGS
# -------------------------------

echo "Setting App Configuration..."

az webapp config appsettings set \
  --name "$WEBAPP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --settings \
    AzureSearch__Endpoint="https://${SEARCH_SERVICE}.search.windows.net" \
    AzureSearch__Key="$SEARCH_KEY" \
    AzureSearch__Index="legal-index" \
    AzureOpenAI__Endpoint="https://${OPENAI_RESOURCE}.openai.azure.com/" \
    AzureOpenAI__Key="$OPENAI_KEY" \
    AzureOpenAI__Deployment="$OPENAI_DEPLOYMENT" \
  --output none

# -------------------------------
# DEPLOY
# -------------------------------

echo "Deploying to Azure..."
az webapp deploy \
  --resource-group "$RESOURCE_GROUP" \
  --name "$WEBAPP_NAME" \
  --src-path ./publish.zip \
  --type zip \
  --output none

# -------------------------------
# RESTART
# -------------------------------

echo "Restarting App..."
az webapp restart \
  --name "$WEBAPP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --output none

echo "=========================================="
echo "DEPLOYMENT COMPLETE"
echo "URL: https://${WEBAPP_NAME}.azurewebsites.net"
echo "=========================================="
