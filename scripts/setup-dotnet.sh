#!/usr/bin/env bash
set -euo pipefail

# Installs .NET 8 SDK on Ubuntu/Debian runners.
# Supports proxy-based environments and prints actionable diagnostics.

DOTNET_VERSION="${DOTNET_VERSION:-8.0}"

have_cmd() { command -v "$1" >/dev/null 2>&1; }

print_proxy_hint() {
  echo ""
  echo "Proxy diagnostics:"
  env | grep -Ei '^(http_proxy|https_proxy|HTTP_PROXY|HTTPS_PROXY|NO_PROXY|no_proxy)=' || true
}

if have_cmd dotnet; then
  echo "✅ dotnet already installed"
  dotnet --info
  exit 0
fi

echo "dotnet not found. Attempting apt-based install of dotnet-sdk-${DOTNET_VERSION}."

if ! have_cmd apt-get; then
  echo "❌ apt-get is not available on this host."
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
  echo "Try allowing these hosts through your outbound proxy:"
  echo "  - archive.ubuntu.com"
  echo "  - security.ubuntu.com"
  echo "  - api.nuget.org"
  exit 1
fi

apt-get install -y "dotnet-sdk-${DOTNET_VERSION}"

echo "✅ dotnet installed successfully"
dotnet --info
