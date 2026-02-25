#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# setup-entra.sh - Register Entra ID app for Legal AI Assistant
# Creates an app registration with the correct permissions
# for authenticating attorneys via Microsoft Entra ID.
# ============================================================

usage() {
    echo "Usage: $0 -n <app-name> [-r <redirect-uri>]"
    echo ""
    echo "Options:"
    echo "  -n  App registration display name (required)"
    echo "  -r  Redirect URI (default: https://<app-name>.azurewebsites.net/.auth/login/aad/callback)"
    echo ""
    echo "Example:"
    echo "  $0 -n agent13-app-prod"
    exit 1
}

APP_NAME=""
REDIRECT_URI=""

while getopts "n:r:h" opt; do
    case $opt in
        n) APP_NAME="$OPTARG" ;;
        r) REDIRECT_URI="$OPTARG" ;;
        h) usage ;;
        *) usage ;;
    esac
done

if [ -z "$APP_NAME" ]; then
    echo "Error: App name is required."
    usage
fi

if [ -z "$REDIRECT_URI" ]; then
    REDIRECT_URI="https://${APP_NAME}.azurewebsites.net/.auth/login/aad/callback"
fi

echo "=== Entra ID App Registration Setup ==="
echo "App Name:     $APP_NAME"
echo "Redirect URI: $REDIRECT_URI"
echo ""

# Check prerequisites
if ! command -v az &> /dev/null; then
    echo "Error: Azure CLI (az) is not installed."
    exit 1
fi

# Ensure logged in
az account show > /dev/null 2>&1 || {
    echo "Error: Not logged in. Run 'az login' first."
    exit 1
}

TENANT_ID=$(az account show --query tenantId -o tsv)
echo "Tenant ID: $TENANT_ID"

# Create the app registration
echo ""
echo "Creating app registration..."
APP_ID=$(az ad app create \
    --display-name "Legal AI Assistant - ${APP_NAME}" \
    --sign-in-audience "AzureADMyOrg" \
    --web-redirect-uris "$REDIRECT_URI" \
    --enable-id-token-issuance true \
    --query appId -o tsv)

echo "App (Client) ID: $APP_ID"

# Set the identifier URI
echo "Setting identifier URI..."
az ad app update --id "$APP_ID" \
    --identifier-uris "api://${APP_ID}"

# Add Microsoft Graph permissions (User.Read for sign-in)
echo "Adding API permissions..."
az ad app permission add \
    --id "$APP_ID" \
    --api 00000003-0000-0000-c000-000000000000 \
    --api-permissions e1fe6dd8-ba31-4d61-89e7-88639da4683d=Scope

# Create a service principal
echo "Creating service principal..."
az ad sp create --id "$APP_ID" > /dev/null 2>&1 || true

# Create client secret
echo "Creating client secret..."
SECRET=$(az ad app credential reset \
    --id "$APP_ID" \
    --display-name "legal-ai-secret" \
    --years 2 \
    --query password -o tsv)

echo ""
echo "=== Setup Complete ==="
echo ""
echo "Save these values for your Bicep deployment:"
echo "  entraClientId = '${APP_ID}'"
echo ""
echo "Store the client secret in Key Vault:"
echo "  az keyvault secret set --vault-name <your-kv> --name entra-client-secret --value '${SECRET}'"
echo ""
echo "To deploy with Entra ID auth enabled:"
echo "  az deployment group create \\"
echo "    --resource-group <rg-name> \\"
echo "    --template-file infra/main.bicep \\"
echo "    --parameters environment=prod entraClientId='${APP_ID}'"
