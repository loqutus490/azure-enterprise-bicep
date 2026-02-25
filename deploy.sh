#!/bin/bash
set -e

RESOURCE_GROUP="${RESOURCE_GROUP:-agent13-dev-rg}"
PROJECT_PATH="./src/LegalRagApp.csproj"

echo "--------------------------------------------------"
echo "🚀 CLEAN .NET 8 FRAMEWORK-DEPENDENT DEPLOYMENT"
echo "--------------------------------------------------"

echo "🔎 Fetching Web App name..."
WEBAPP_NAME=$(az webapp list --resource-group $RESOURCE_GROUP --query "[0].name" -o tsv)

echo "🔓 Enabling SCM basic auth (required for zip deploy)..."
az resource update \
  --resource-group $RESOURCE_GROUP \
  --name scm \
  --namespace Microsoft.Web \
  --resource-type basicPublishingCredentialsPolicies \
  --parent "sites/$WEBAPP_NAME" \
  --set properties.allow=true \
  --output none

echo "🔨 Publishing (framework-dependent)..."
rm -rf publish publish.zip
dotnet publish "$PROJECT_PATH" -c Release -o ./publish

echo "📦 Packaging..."
(cd publish && zip -r ../publish.zip .)

echo "🚀 Deploying..."
az webapp deploy \
  --resource-group $RESOURCE_GROUP \
  --name "$WEBAPP_NAME" \
  --src-path ./publish.zip \
  --type zip

echo "🔄 Restarting app..."
az webapp restart \
  --resource-group $RESOURCE_GROUP \
  --name "$WEBAPP_NAME"

echo "✅ Deployment complete"
echo "🔗 https://$WEBAPP_NAME.azurewebsites.net"
