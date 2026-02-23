#!/bin/bash

set -e

echo "=========================================="
echo "🚀 FULL RAG DEPLOYMENT SCRIPT"
echo "=========================================="

# -------------------------------
# CONFIGURATION
# -------------------------------

RESOURCE_GROUP="my-app-rg"
WEBAPP_NAME="legal-rag-pcbbfwvldsbgg"
SEARCH_SERVICE="agent13-legal-search-wus2"
OPENAI_RESOURCE="agent13-openai-wus2-02"
OPENAI_DEPLOYMENT="gpt-4o-mini"

# -------------------------------
# VERIFY RESOURCES EXIST
# -------------------------------

echo "🔎 Verifying Azure Search..."
az search service show \
  --name $SEARCH_SERVICE \
  --resource-group $RESOURCE_GROUP \
  > /dev/null

echo "🔎 Verifying Azure OpenAI..."
az cognitiveservices account show \
  --name $OPENAI_RESOURCE \
  --resource-group $RESOURCE_GROUP \
  > /dev/null

# -------------------------------
# FETCH KEYS AUTOMATICALLY
# -------------------------------

echo "🔑 Fetching Azure Search Admin Key..."
SEARCH_KEY=$(az search admin-key show \
  --service-name $SEARCH_SERVICE \
  --resource-group $RESOURCE_GROUP \
  --query primaryKey -o tsv)

echo "🔑 Fetching Azure OpenAI Key..."
OPENAI_KEY=$(az cognitiveservices account keys list \
  --name $OPENAI_RESOURCE \
  --resource-group $RESOURCE_GROUP \
  --query key1 -o tsv)

# -------------------------------
# BUILD + PUBLISH
# -------------------------------

echo "🔨 Cleaning previous builds..."
rm -rf src/bin src/obj publish publish.zip

echo "🔨 Publishing .NET 8 app..."
dotnet publish src -c Release -o publish

# -------------------------------
# ZIP PACKAGE
# -------------------------------

echo "📦 Packaging..."
cd publish
zip -r ../publish.zip .
cd ..

# -------------------------------
# SET APP SETTINGS (SAFE)
# -------------------------------

echo "⚙️ Setting App Configuration..."

az webapp config appsettings set \
  --name $WEBAPP_NAME \
  --resource-group $RESOURCE_GROUP \
  --settings \
  AzureSearch__Endpoint="https://${SEARCH_SERVICE}.search.windows.net" \
  AzureSearch__Key="$SEARCH_KEY" \
  AzureSearch__Index="legal-index" \
  AzureOpenAI__Endpoint="https://${OPENAI_RESOURCE}.openai.azure.com/" \
  AzureOpenAI__Key="$OPENAI_KEY" \
  AzureOpenAI__Deployment="$OPENAI_DEPLOYMENT"

# -------------------------------
# DEPLOY
# -------------------------------

echo "🚀 Deploying to Azure..."
az webapp deployment source config-zip \
  --resource-group $RESOURCE_GROUP \
  --name $WEBAPP_NAME \
  --src publish.zip

# -------------------------------
# RESTART
# -------------------------------

echo "🔄 Restarting App..."
az webapp restart \
  --name $WEBAPP_NAME \
  --resource-group $RESOURCE_GROUP

echo "=========================================="
echo "✅ DEPLOYMENT COMPLETE"
echo "🔗 https://${WEBAPP_NAME}.azurewebsites.net"
echo "=========================================="