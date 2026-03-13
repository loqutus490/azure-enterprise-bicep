#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo "== Step 1/4: .NET setup check =="
./scripts/setup-dotnet.sh || true

echo
echo "== Step 2/4: Build =="
if command -v dotnet >/dev/null 2>&1; then
  dotnet build legal-rag-platform.sln
else
  echo "[warn] dotnet is not available; skipping build."
fi

echo
echo "== Step 3/4: Test =="
if command -v dotnet >/dev/null 2>&1; then
  dotnet test legal-rag-platform.sln
else
  echo "[warn] dotnet is not available; skipping tests."
fi

echo
echo "== Step 4/4: Branch cleanup dry run =="
if git remote get-url origin >/dev/null 2>&1; then
  ./scripts/cleanup-branches.sh --remote origin --base main
else
  echo "[warn] origin remote is not configured; skipping branch cleanup dry-run."
fi

echo
echo "Done. If you saw warnings, follow docs/DOTNET_SETUP.md and docs/FIRST_STEPS_LOCAL_SETUP.md."
