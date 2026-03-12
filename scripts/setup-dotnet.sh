#!/usr/bin/env bash
set -euo pipefail

# Installs .NET 8 SDK on Ubuntu/Debian runners.
# Supports proxy-based environments and prints actionable diagnostics.
# Falls back to dotnet-install script when apt-based installation is blocked.

DOTNET_VERSION="${DOTNET_VERSION:-8.0}"
USE_DOTNET_INSTALL_FALLBACK="${USE_DOTNET_INSTALL_FALLBACK:-true}"


usage() {
  cat <<USAGE
Usage: $(basename "$0") [-h|--help]

Installs .NET SDK (${DOTNET_VERSION} channel by default) using apt-get when available,
with optional fallback to dotnet-install.sh.

Environment variables:
  DOTNET_VERSION                  SDK channel/version to install (default: 8.0)
  USE_DOTNET_INSTALL_FALLBACK     true|false (default: true)
USAGE
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

have_cmd() { command -v "$1" >/dev/null 2>&1; }

print_proxy_hint() {
  echo ""
  echo "Proxy diagnostics:"
  env | grep -Ei '^(http_proxy|https_proxy|HTTP_PROXY|HTTPS_PROXY|NO_PROXY|no_proxy)=' || true
}

print_allowlist_hint() {
  echo "Try allowing these hosts through your outbound proxy:"
  echo "  - archive.ubuntu.com"
  echo "  - security.ubuntu.com"
  echo "  - api.nuget.org"
  echo "  - builds.dotnet.microsoft.com"
  echo "  - dotnetcli.azureedge.net"
}

install_with_dotnet_script() {
  if ! have_cmd curl; then
    echo "❌ curl is required for dotnet-install fallback but is not available."
    return 1
  fi

  local install_dir="${HOME}/.dotnet"
  echo "Attempting fallback installation using dotnet-install script (channel ${DOTNET_VERSION})..."
  if ! curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh; then
    echo "❌ Failed to download dotnet-install.sh (likely proxy allowlist issue)."
    return 1
  fi

  chmod +x /tmp/dotnet-install.sh
  if ! /tmp/dotnet-install.sh --channel "${DOTNET_VERSION}" --install-dir "${install_dir}"; then
    echo "❌ dotnet-install.sh execution failed."
    return 1
  fi

  if [[ -x "${install_dir}/dotnet" ]]; then
    echo "✅ dotnet installed via dotnet-install to ${install_dir}"
    echo "Add these lines to your shell profile if needed:"
    echo "  export DOTNET_ROOT=${install_dir}"
    echo '  export PATH="$DOTNET_ROOT:$PATH"'
    export DOTNET_ROOT="${install_dir}"
    export PATH="${DOTNET_ROOT}:${PATH}"
    dotnet --info
    return 0
  fi

  echo "❌ dotnet-install fallback did not produce a working dotnet binary."
  return 1
}

if have_cmd dotnet; then
  echo "✅ dotnet already installed"
  dotnet --info
  exit 0
fi

echo "dotnet not found. Attempting apt-based install of dotnet-sdk-${DOTNET_VERSION}."

if ! have_cmd apt-get; then
  echo "❌ apt-get is not available on this host."
  if [[ "${USE_DOTNET_INSTALL_FALLBACK}" == "true" ]]; then
    install_with_dotnet_script && exit 0
  fi
  echo "Use a base image with .NET preinstalled (recommended) or install manually."
  exit 1
fi

set +e
apt-get update
APT_UPDATE_EXIT=$?
set -e

if [[ $APT_UPDATE_EXIT -ne 0 ]]; then
  echo "❌ apt-get update failed. This is usually proxy or network policy related."
  print_proxy_hint

  if [[ "${USE_DOTNET_INSTALL_FALLBACK}" == "true" ]]; then
    if install_with_dotnet_script; then
      exit 0
    fi
  fi

  print_allowlist_hint
  exit 1
fi

apt-get install -y "dotnet-sdk-${DOTNET_VERSION}"

echo "✅ dotnet installed successfully"
dotnet --info
