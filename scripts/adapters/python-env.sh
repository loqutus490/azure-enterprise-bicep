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

export AZURE_OPENAI_ENDPOINT="${RAG_AZURE_OPENAI_ENDPOINT:-}"
export AZURE_OPENAI_CHAT_DEPLOYMENT="${RAG_AZURE_OPENAI_CHAT_DEPLOYMENT:-}"
export AZURE_OPENAI_EMBEDDING_DEPLOYMENT="${RAG_AZURE_OPENAI_EMBEDDING_DEPLOYMENT:-}"
export AZURE_OPENAI_EMBEDDING_DIMENSIONS="${RAG_AZURE_OPENAI_EMBEDDING_DIMENSIONS:-}"

export AZURE_SEARCH_ENDPOINT="${RAG_AZURE_SEARCH_ENDPOINT:-}"
export AZURE_SEARCH_INDEX="${RAG_AZURE_SEARCH_INDEX:-}"
