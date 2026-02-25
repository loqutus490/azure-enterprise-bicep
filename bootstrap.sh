#!/bin/bash

set -e

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

echo "🚀 Deploying GPT-4o-mini model..."
az cognitiveservices account deployment create \
  --name $OPENAI_RESOURCE \
  --resource-group $RESOURCE_GROUP \
  --deployment-name $OPENAI_DEPLOYMENT \
  --model-name gpt-4o-mini \
  --model-version "2024-07-18" \
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
# FETCH KEYS
# ==========================

echo "🔑 Fetching Azure Search Key..."
SEARCH_KEY=$(az search admin-key show \
  --service-name $SEARCH_SERVICE \
  --resource-group $RESOURCE_GROUP \
  --query primaryKey -o tsv)

echo "🔑 Fetching Azure OpenAI Key..."
OPENAI_KEY=$(az cognitiveservices account keys list \
  --name $OPENAI_RESOURCE \
  --resource-group $RESOURCE_GROUP \
  --query key1 -o tsv)

# ==========================
# CONFIGURE APP SETTINGS
# ==========================

echo "⚙️ Configuring App Settings..."

az webapp config appsettings set \
  --name $WEBAPP_NAME \
  --resource-group $RESOURCE_GROUP \
  --settings \
  AzureSearch__Endpoint="https://${SEARCH_SERVICE}.search.windows.net" \
  AzureSearch__Key="$SEARCH_KEY" \
  AzureSearch__Index="legal-index" \
  AzureOpenAI__Endpoint="https://${OPENAI_RESOURCE}.openai.azure.com/" \
  AzureOpenAI__Key="$OPENAI_KEY" \
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
az webapp deployment source config-zip \
  --resource-group $RESOURCE_GROUP \
  --name $WEBAPP_NAME \
  --src publish.zip \
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