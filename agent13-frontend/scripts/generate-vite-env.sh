#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="${1:-../.env.shared}"
OUT_FILE="${2:-.env.local}"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "❌ Shared env file not found: $ENV_FILE" >&2
  echo "   Copy ../.env.shared.example -> ../.env.shared and fill in your values." >&2
  exit 1
fi

set -a
# shellcheck source=/dev/null
source "$ENV_FILE"
set +a

cat > "$OUT_FILE" <<EOF
VITE_AZURE_TENANT_ID=${RAG_AZURE_TENANT_ID:-}
VITE_AZURE_FRONTEND_CLIENT_ID=${RAG_AZURE_ENTRA_FRONTEND_CLIENT_ID:-}
VITE_AZURE_BACKEND_CLIENT_ID=${RAG_AZURE_ENTRA_API_CLIENT_ID:-}
VITE_AGENT13_API_BASE_URL=${RAG_API_BASE_URL:-http://localhost:5176}
EOF

echo "✅ Generated ${OUT_FILE} from ${ENV_FILE}"
