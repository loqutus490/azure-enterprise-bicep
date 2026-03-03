#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="${1:-./.env.shared}"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "❌ Shared env file not found: $ENV_FILE" >&2
  echo "   Copy .env.shared.example -> .env.shared and fill in your values." >&2
  exit 1
fi

set -a
# shellcheck source=/dev/null
source "$ENV_FILE"
set +a

export ASPNETCORE_ENVIRONMENT="${RAG_ENVIRONMENT:-Development}"

export AzureAd__Instance="https://login.microsoftonline.com/"
export AzureAd__TenantId="${RAG_AZURE_TENANT_ID:-}"
export AzureAd__ClientId="${RAG_AZURE_ENTRA_API_CLIENT_ID:-}"
export AzureAd__Audience="${RAG_AZURE_ENTRA_API_CLIENT_ID:-}"

export Authorization__RequiredScope="${RAG_AUTH_REQUIRED_SCOPE:-access_as_user}"
export Authorization__RequiredRole="${RAG_AUTH_REQUIRED_ROLE:-Api.Access}"
export Authorization__BypassAuthInDevelopment="${RAG_AUTH_BYPASS_IN_DEV:-false}"

export AzureOpenAI__Endpoint="${RAG_AZURE_OPENAI_ENDPOINT:-}"
export AzureOpenAI__Deployment="${RAG_AZURE_OPENAI_CHAT_DEPLOYMENT:-}"
export AzureOpenAI__EmbeddingDeployment="${RAG_AZURE_OPENAI_EMBEDDING_DEPLOYMENT:-}"

export AzureSearch__Endpoint="${RAG_AZURE_SEARCH_ENDPOINT:-}"
export AzureSearch__Index="${RAG_AZURE_SEARCH_INDEX:-}"

# Convert comma-separated values into .NET array env vars:
# Authorization__AllowedClientAppIds__0=value
# Cors__AllowedOrigins__0=value
set_dotnet_array_from_csv() {
  local csv="${1:-}"
  local prefix="$2"
  local i=0
  local -a items=()

  IFS=',' read -r -a items <<< "$csv"
  for raw in "${items[@]-}"; do
    local item
    item="$(echo "$raw" | xargs)"
    [[ -z "$item" ]] && continue
    export "${prefix}__${i}=${item}"
    i=$((i + 1))
  done
}

set_dotnet_array_from_csv "${RAG_AZURE_ENTRA_ALLOWED_CLIENT_APP_IDS:-}" "Authorization__AllowedClientAppIds"
set_dotnet_array_from_csv "${RAG_CORS_ALLOWED_ORIGINS:-}" "Cors__AllowedOrigins"
