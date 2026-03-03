#!/bin/bash
set -e

# Resolve repo root so the script works from any directory
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

DOTNET_ADAPTER="./scripts/adapters/dotnet-env.sh"
ENV_FILE="${1:-}"

if [ -z "$ENV_FILE" ]; then
  if [ -f "./.env.shared" ]; then
    ENV_FILE="./.env.shared"
  else
    ENV_FILE="./local.env"
  fi
fi

if [ ! -f "$ENV_FILE" ]; then
  echo "❌ Config file not found: $ENV_FILE"
  echo "   Copy .env.shared.example -> .env.shared (recommended)"
  echo "   or local.env.example -> local.env (legacy)."
  exit 1
fi

echo "📂 Loading config from $ENV_FILE"
if grep -q '^RAG_' "$ENV_FILE"; then
  # shellcheck source=/dev/null
  source "$DOTNET_ADAPTER" "$ENV_FILE"
else
  set -a
  # shellcheck source=/dev/null
  source "$ENV_FILE"
  set +a
fi

required_vars=(
  "AzureSearch__Endpoint"
  "AzureSearch__Index"
  "AzureOpenAI__Endpoint"
  "AzureOpenAI__Deployment"
  "AzureOpenAI__EmbeddingDeployment"
)

for var_name in "${required_vars[@]}"; do
  value="${!var_name:-}"
  if [ -z "$value" ]; then
    echo "❌ Missing required config: $var_name"
    echo "   Update $ENV_FILE with a real value."
    exit 1
  fi

  if [[ "$value" == *"<"* || "$value" == *">"* ]]; then
    echo "❌ Placeholder value detected for: $var_name"
    echo "   Replace template values in $ENV_FILE before running locally."
    exit 1
  fi
done

echo "🚀 Starting LegalRagApp locally..."
dotnet run --project ./src/LegalRagApp.csproj --no-restore
