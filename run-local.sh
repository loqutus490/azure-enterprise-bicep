#!/bin/bash
set -e

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

echo "🚀 Starting LegalRagApp locally..."
dotnet run --project ./src/LegalRagApp.csproj
