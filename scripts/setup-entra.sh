#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# setup-entra.sh - Register Entra ID app for Legal AI Assistant
# Creates an app registration with the correct permissions
# for authenticating attorneys via Microsoft Entra ID.
#
# Idempotent: re-running with the same name reuses the existing
# app registration instead of creating a duplicate.
# ============================================================

usage() {
    echo "Usage: $0 -n <app-name> [-r <redirect-uri>] [-v <keyvault-name>]"
    echo ""
    echo "Options:"
    echo "  -n  App registration display name (required)"
    echo "  -r  Redirect URI (default: https://<app-name>.azurewebsites.net/.auth/login/aad/callback)"
    echo "  -v  Key Vault name to store the client secret (optional)"
    echo ""
    echo "Example:"
    echo "  $0 -n agent13-app-prod"
    echo "  $0 -n agent13-app-prod -v agent13-kv-prod"
    exit 1
}

APP_NAME=""
REDIRECT_URI=""
KV_NAME=""

while getopts "n:r:v:h" opt; do
    case $opt in
        n) APP_NAME="$OPTARG" ;;
        r) REDIRECT_URI="$OPTARG" ;;
        v) KV_NAME="$OPTARG" ;;
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

DISPLAY_NAME="Legal AI Assistant - ${APP_NAME}"

echo "=== Entra ID App Registration Setup ==="
echo "App Name:     $DISPLAY_NAME"
echo "Redirect URI: $REDIRECT_URI"
if [ -n "$KV_NAME" ]; then
    echo "Key Vault:    $KV_NAME (secret will be stored automatically)"
fi
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

# Check for existing app registration (idempotency)
echo ""
echo "Checking for existing app registration..."
EXISTING_APP_ID=$(az ad app list \
    --display-name "$DISPLAY_NAME" \
    --query "[0].appId" -o tsv 2>/dev/null || true)

if [ -n "$EXISTING_APP_ID" ] && [ "$EXISTING_APP_ID" != "None" ]; then
    echo "Found existing app registration: $EXISTING_APP_ID"
    echo "Reusing existing registration (updating redirect URI)."
    APP_ID="$EXISTING_APP_ID"

    az ad app update --id "$APP_ID" \
        --web-redirect-uris "$REDIRECT_URI"
else
    # Create the app registration
    echo "Creating new app registration..."
    APP_ID=$(az ad app create \
        --display-name "$DISPLAY_NAME" \
        --sign-in-audience "AzureADMyOrg" \
        --web-redirect-uris "$REDIRECT_URI" \
        --enable-id-token-issuance true \
        --query appId -o tsv)

    echo "Created app: $APP_ID"
fi

# Set the identifier URI
echo "Setting identifier URI..."
az ad app update --id "$APP_ID" \
    --identifier-uris "api://${APP_ID}"

# Add Microsoft Graph User.Read permission (e1fe6dd8... = User.Read delegated scope)
echo "Adding API permissions (User.Read)..."
az ad app permission add \
    --id "$APP_ID" \
    --api 00000003-0000-0000-c000-000000000000 \
    --api-permissions e1fe6dd8-ba31-4d61-89e7-88639da4683d=Scope 2>/dev/null || true

# Grant admin consent so users don't see a consent prompt
echo "Granting admin consent..."
az ad app permission admin-consent --id "$APP_ID" 2>/dev/null || {
    echo "Warning: Could not grant admin consent. You may need Global Admin privileges."
    echo "  Run manually: az ad app permission admin-consent --id $APP_ID"
}

# Create service principal (idempotent — ignores error if it exists)
echo "Ensuring service principal exists..."
az ad sp create --id "$APP_ID" > /dev/null 2>&1 || true

# Create client secret
echo "Creating client secret (valid for 2 years)..."
SECRET=$(az ad app credential reset \
    --id "$APP_ID" \
    --display-name "legal-ai-secret" \
    --years 2 \
    --query password -o tsv)

# Store in Key Vault if name was provided
if [ -n "$KV_NAME" ]; then
    echo "Storing client secret in Key Vault '${KV_NAME}'..."
    az keyvault secret set \
        --vault-name "$KV_NAME" \
        --name "entra-client-secret" \
        --value "$SECRET" \
        --output none
    echo "Secret stored as 'entra-client-secret' in $KV_NAME."
fi

echo ""
echo "=== Setup Complete ==="
echo ""
echo "App (Client) ID: ${APP_ID}"
echo "Tenant ID:       ${TENANT_ID}"
echo ""
if [ -z "$KV_NAME" ]; then
    echo "IMPORTANT: Store this client secret securely (it will not be shown again):"
    echo "  ${SECRET}"
    echo ""
    echo "To store in Key Vault:"
    echo "  az keyvault secret set --vault-name <your-kv> --name entra-client-secret --value '${SECRET}'"
    echo ""
fi
echo "To deploy with Entra ID auth enabled:"
echo "  az deployment group create \\"
echo "    --resource-group <rg-name> \\"
echo "    --template-file infra/main.bicep \\"
echo "    --parameters environment=prod entraClientId='${APP_ID}'"
