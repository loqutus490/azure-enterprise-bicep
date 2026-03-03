#!/usr/bin/env bash
set -euo pipefail

# Resolve repo root so the script works from any directory
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

ENV_FILE="${1:-./.env.shared}"

# shellcheck source=/dev/null
source ./scripts/adapters/python-env.sh "$ENV_FILE"

exec python3 ./scripts/test_embedding.py
