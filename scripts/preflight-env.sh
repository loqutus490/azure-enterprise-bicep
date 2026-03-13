#!/usr/bin/env bash
set -euo pipefail

# Environment preflight for local/CI validation in constrained networks.
# Checks runtime toolchain and outbound connectivity for package/runtime dependencies.

TIMEOUT_SECONDS="${PREFLIGHT_TIMEOUT_SECONDS:-8}"
SHOW_ENV_PROXY="${PREFLIGHT_SHOW_PROXY_ENV:-true}"

ok_count=0
warn_count=0
fail_count=0

ok() { echo "✅ $*"; ok_count=$((ok_count+1)); }
warn() { echo "⚠️  $*"; warn_count=$((warn_count+1)); }
fail() { echo "❌ $*"; fail_count=$((fail_count+1)); }

check_cmd() {
  local cmd="$1"
  local purpose="$2"
  if command -v "$cmd" >/dev/null 2>&1; then
    ok "Command '$cmd' is available ($purpose)."
    return 0
  fi

  warn "Command '$cmd' is missing ($purpose)."
  return 1
}

check_url() {
  local url="$1"
  local label="$2"

  local output
  set +e
  output="$(curl -I -L -sS --max-time "$TIMEOUT_SECONDS" "$url" 2>&1)"
  local code=$?
  set -e

  if [[ $code -eq 0 ]]; then
    ok "Reachable: $label ($url)."
    return 0
  fi

  if echo "$output" | grep -qi "403"; then
    warn "Blocked by policy/proxy: $label ($url) -> HTTP 403."
    return 0
  fi

  warn "Unreachable: $label ($url) (curl exit $code)."
  return 0
}

print_section() {
  echo
  echo "=== $1 ==="
}

print_section "Toolchain checks"
check_cmd dotnet "build and test" || true
check_cmd docker "containerized build/test fallback" || true
check_cmd az "Azure deployment/infra checks" || true
check_cmd node "frontend build" || true
check_cmd npm "frontend package manager" || true

if command -v dotnet >/dev/null 2>&1; then
  set +e
  dotnet --version >/dev/null 2>&1
  code=$?
  set -e
  if [[ $code -eq 0 ]]; then
    ok "dotnet CLI executes successfully."
  else
    fail "dotnet exists but failed to execute."
  fi
fi

if command -v docker >/dev/null 2>&1; then
  set +e
  docker --version >/dev/null 2>&1
  code=$?
  set -e
  if [[ $code -eq 0 ]]; then
    ok "docker CLI executes successfully."
  else
    fail "docker exists but failed to execute."
  fi
fi

print_section "Network connectivity checks"
check_url "http://archive.ubuntu.com/ubuntu" "Ubuntu apt mirror"
check_url "http://security.ubuntu.com/ubuntu" "Ubuntu security mirror"
check_url "https://api.nuget.org/v3/index.json" "NuGet"
check_url "https://download.docker.com/linux/static/stable/x86_64/" "Docker binaries"
check_url "https://registry-1.docker.io/v2/" "Docker Hub registry API"

if [[ "$SHOW_ENV_PROXY" == "true" ]]; then
  print_section "Proxy environment"
  env | grep -Ei '^(http_proxy|https_proxy|HTTP_PROXY|HTTPS_PROXY|NO_PROXY|no_proxy)=' || warn "No proxy variables detected."
fi

print_section "Summary"
echo "Passed:  $ok_count"
echo "Warnings: $warn_count"
echo "Failed: $fail_count"

if [[ $fail_count -gt 0 ]]; then
  exit 1
fi

exit 0
