#!/bin/bash
set -e

# Resolve repo root so the script works from any directory
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

ENV_FILE="${1:-./local.env}"

if [ ! -f "$ENV_FILE" ]; then
  echo "❌ Config file not found: $ENV_FILE"
  echo "   Copy local.env.example → local.env and fill in your values."
  exit 1
fi

echo "📂 Loading config from $ENV_FILE"
set -a
# shellcheck source=/dev/null
source "$ENV_FILE"
set +a

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
