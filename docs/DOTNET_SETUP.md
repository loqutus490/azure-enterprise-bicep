# .NET SDK Setup (Ubuntu / CI Containers)

Use this runbook when `dotnet` is missing and `dotnet build`/`dotnet test` fail.

## 1) Quick install

```bash
./scripts/setup-dotnet.sh
```

This script:
- detects whether `dotnet` is already installed,
- installs `dotnet-sdk-8.0` via `apt-get` when package feeds are reachable,
- falls back to Microsoft's `dotnet-install.sh` when `apt-get update` is blocked,
- prints proxy/network diagnostics if installation fails.

## 2) Verify

```bash
dotnet --info
dotnet build
dotnet test
```

## 3) If install fails with 403 / tunnel / proxy errors

Your environment likely blocks outbound package traffic. Allow the following hosts in your proxy/firewall policy:

- `archive.ubuntu.com`
- `security.ubuntu.com`
- `api.nuget.org`

## 4) Recommended for CI: pre-baked image

For reliable builds, use a base image that already includes .NET 8.

Example Docker base:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /src
COPY . .
RUN dotnet restore && dotnet build --no-restore
```

This avoids runtime installation and proxy issues during the pipeline.


## 5) If your container uses an outbound proxy

If you see `403 Forbidden` from `apt-get update`, your proxy is blocking Ubuntu package feeds. Ensure allowlist coverage for:

- `archive.ubuntu.com`
- `security.ubuntu.com`
- `api.nuget.org`
- `builds.dotnet.microsoft.com`
- `dotnetcli.azureedge.net`

Also confirm your proxy env vars are set correctly (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`).

## 6) Fastest reliable option

If you control the dev container/image, use a pre-baked SDK image to avoid runtime install failures:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /src
COPY . .
RUN dotnet restore && dotnet build --no-restore && dotnet test --no-build
```
