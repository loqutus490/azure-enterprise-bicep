# .NET SDK Setup (Ubuntu / CI Containers)

Use this runbook when `dotnet` is missing and `dotnet build`/`dotnet test` fail.

## 1) Quick install

```bash
./scripts/setup-dotnet.sh
```

This script:
- detects whether `dotnet` is already installed,
- installs `dotnet-sdk-8.0` via `apt-get`,
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
