#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

MODE="${1:-local}"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

PASS_COUNT=0
FAIL_COUNT=0
SKIP_COUNT=0

pass() {
  echo -e "${GREEN}[PASS]${NC} $1"
  PASS_COUNT=$((PASS_COUNT + 1))
}

fail() {
  echo -e "${RED}[FAIL]${NC} $1"
  FAIL_COUNT=$((FAIL_COUNT + 1))
}

skip() {
  echo -e "${YELLOW}[SKIP]${NC} $1"
  SKIP_COUNT=$((SKIP_COUNT + 1))
}

require_cmd() {
  local cmd="$1"
  if command -v "$cmd" >/dev/null 2>&1; then
    pass "Dependency '$cmd' is available"
  else
    fail "Dependency '$cmd' is missing"
  fi
}

run_check() {
  local name="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    pass "$name"
  else
    fail "$name"
  fi
}

echo "========================================"
echo "Production Readiness Check"
echo "Mode: $MODE"
echo "Repo: $REPO_ROOT"
echo "========================================"

# Base dependencies
require_cmd dotnet
require_cmd az

# Local validation gates
run_check "dotnet restore" dotnet restore
run_check "dotnet build (Release)" dotnet build ./src/LegalRagApp.csproj -c Release
run_check "dotnet test" dotnet test --nologo
run_check "Bicep compile: infra/main.bicep" az bicep build --file infra/main.bicep
run_check "Bicep compile: infra/modules/appservice.bicep" az bicep build --file infra/modules/appservice.bicep
run_check "Bicep compile: infra/modules/openai.bicep" az bicep build --file infra/modules/openai.bicep

if rg -n "param allowedClientApplications = \[\]" infra/params/prod.bicepparam >/dev/null 2>&1; then
  fail "prod.bicepparam has empty allowedClientApplications"
else
  pass "prod.bicepparam includes non-empty allowedClientApplications"
fi

if rg -n "param entraClientId = ''" infra/params/prod.bicepparam >/dev/null 2>&1; then
  fail "prod.bicepparam has empty entraClientId"
else
  pass "prod.bicepparam includes non-empty entraClientId"
fi

if [[ "$MODE" == "azure" ]]; then
  : "${RESOURCE_GROUP:?RESOURCE_GROUP is required in azure mode}"
  : "${APP_NAME:?APP_NAME is required in azure mode}"

  if az group show --name "$RESOURCE_GROUP" >/dev/null 2>&1; then
    pass "Resource group '$RESOURCE_GROUP' exists"
  else
    fail "Resource group '$RESOURCE_GROUP' not found"
  fi

  if az webapp show --resource-group "$RESOURCE_GROUP" --name "$APP_NAME" >/dev/null 2>&1; then
    pass "Web App '$APP_NAME' exists"
  else
    fail "Web App '$APP_NAME' not found"
  fi

  PING_STATUS="$(curl -s -o /dev/null -w "%{http_code}" "https://${APP_NAME}.azurewebsites.net/ping" || true)"
  if [[ "$PING_STATUS" == "200" || "$PING_STATUS" == "401" ]]; then
    pass "/ping endpoint health status acceptable (${PING_STATUS})"
  else
    fail "/ping endpoint expected 200 or 401, got ${PING_STATUS:-unknown}"
  fi

  ASK_STATUS="$(curl -s -o /dev/null -w "%{http_code}" -X POST "https://${APP_NAME}.azurewebsites.net/ask" -H "Content-Type: application/json" -d '{"question":"hello"}' || true)"
  if [[ "$ASK_STATUS" == "401" ]]; then
    pass "/ask unauthenticated returns 401"
  else
    fail "/ask unauthenticated expected 401, got ${ASK_STATUS:-unknown}"
  fi
else
  skip "Azure resource checks (set mode to 'azure' to enable)"
fi

echo ""
echo "========================================"
echo "Results"
echo "  Passed: $PASS_COUNT"
echo "  Failed: $FAIL_COUNT"
echo "  Skipped: $SKIP_COUNT"
echo "========================================"

if [[ "$FAIL_COUNT" -gt 0 ]]; then
  exit 1
fi
