#!/bin/bash

set -e

# Resolve repo root so the script works from any directory
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

# ==========================
# CONFIG
# ==========================

LOCATION="westus2"
RESOURCE_GROUP="legal-rag-rg"
SEARCH_SERVICE="legalragsearch$RANDOM"
OPENAI_RESOURCE="legalragopenai$RANDOM"
OPENAI_DEPLOYMENT="gpt-4o"
APP_PLAN="legalrag-plan"
WEBAPP_NAME="legalragapp$RANDOM"

echo "=========================================="
echo "🚀 BOOTSTRAPPING FULL RAG ENVIRONMENT"
echo "=========================================="

# ==========================
# CREATE RESOURCE GROUP
# ==========================

echo "📦 Creating Resource Group..."
az group create \
  --name $RESOURCE_GROUP \
  --location $LOCATION \
  > /dev/null

# ==========================
# CREATE AZURE SEARCH
# ==========================

echo "🔎 Creating Azure Search..."
az search service create \
  --name $SEARCH_SERVICE \
  --resource-group $RESOURCE_GROUP \
  --location $LOCATION \
  --sku basic \
  > /dev/null

# Wait for provisioning
sleep 20

# ==========================
# CREATE AZURE OPENAI
# ==========================

echo "🧠 Creating Azure OpenAI..."
az cognitiveservices account create \
  --name $OPENAI_RESOURCE \
  --resource-group $RESOURCE_GROUP \
  --location $LOCATION \
  --kind OpenAI \
  --sku S0 \
  --yes \
  > /dev/null

sleep 20

echo "🚀 Deploying GPT-4o model..."
az cognitiveservices account deployment create \
  --name "$OPENAI_RESOURCE" \
  --resource-group "$RESOURCE_GROUP" \
  --deployment-name "$OPENAI_DEPLOYMENT" \
  --model-name gpt-4o \
  --model-version "2024-05-13" \
  --model-format OpenAI \
  --scale-settings-scale-type Standard \
  > /dev/null

sleep 20

# ==========================
# CREATE APP SERVICE PLAN
# ==========================

echo "🖥 Creating App Service Plan..."
az appservice plan create \
  --name $APP_PLAN \
  --resource-group $RESOURCE_GROUP \
  --sku B1 \
  --is-linux \
  > /dev/null

# ==========================
# CREATE WEB APP
# ==========================

echo "🌐 Creating Web App..."
az webapp create \
  --resource-group $RESOURCE_GROUP \
  --plan $APP_PLAN \
  --name $WEBAPP_NAME \
  --runtime "DOTNETCORE:8.0" \
  > /dev/null

# ==========================
# CONFIGURE APP SETTINGS
# ==========================

echo "⚙️ Configuring App Settings..."

az webapp config appsettings set \
  --name $WEBAPP_NAME \
  --resource-group $RESOURCE_GROUP \
  --settings \
  AzureSearch__Endpoint="https://${SEARCH_SERVICE}.search.windows.net" \
  AzureSearch__Index="legal-index" \
  AzureOpenAI__Endpoint="https://${OPENAI_RESOURCE}.openai.azure.com/" \
  AzureOpenAI__Deployment="$OPENAI_DEPLOYMENT" \
  > /dev/null

# ==========================
# BUILD + DEPLOY
# ==========================

echo "🔨 Publishing Application..."
rm -rf publish publish.zip
dotnet publish src -c Release -o publish

cd publish
zip -r ../publish.zip .
cd ..

echo "🚀 Deploying Code..."
az webapp deploy \
  --resource-group "$RESOURCE_GROUP" \
  --name "$WEBAPP_NAME" \
  --src-path publish.zip \
  --type zip \
  > /dev/null

# ==========================
# RESTART
# ==========================

az webapp restart \
  --name $WEBAPP_NAME \
  --resource-group $RESOURCE_GROUP \
  > /dev/null

echo "=========================================="
echo "✅ ENVIRONMENT READY"
echo "=========================================="
echo "🔗 URL: https://${WEBAPP_NAME}.azurewebsites.net"
echo "📦 Resource Group: $RESOURCE_GROUP"
echo "🔎 Search Service: $SEARCH_SERVICE"
echo "🧠 OpenAI Resource: $OPENAI_RESOURCE"
echo "=========================================="
