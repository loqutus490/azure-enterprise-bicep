# First Step: Get .NET installed (beginner walkthrough)

If you are new, this is the **first thing** to do before anything else.

Why this first? This project uses .NET. Without .NET installed, `dotnet build` and `dotnet test` will fail immediately.

## Step 1: Open a terminal in the project folder

From your terminal:

```bash
cd /workspace/azure-enterprise-bicep
pwd
```

You should see the path to this repo.

## Step 2: Run the setup script

```bash
./scripts/setup-dotnet.sh --help
./scripts/setup-dotnet.sh
```

What this does:
- checks whether `dotnet` already exists,
- tries to install .NET 8 from Ubuntu package feeds,
- if package install is blocked, tries Microsoft `dotnet-install.sh` fallback,
- prints proxy diagnostics and exactly which hostnames must be allowed.

## Step 3: Verify install

```bash
dotnet --info
```

If this prints SDK information, you are ready for build/tests.

## If it fails with 403 Forbidden

That usually means your outbound proxy/firewall is blocking package downloads.

Ask your IT/admin to allow these hosts:

- `archive.ubuntu.com`
- `security.ubuntu.com`
- `api.nuget.org`
- `builds.dotnet.microsoft.com`
- `dotnetcli.azureedge.net`

Also verify these env vars are set correctly in your shell:

- `HTTP_PROXY`
- `HTTPS_PROXY`
- `NO_PROXY`

## After Step 1 works

Run:

```bash
dotnet build legal-rag-platform.sln
dotnet test legal-rag-platform.sln
```

That is the normal local verification flow.


## About authorization bypass warnings

If you are intentionally running in Development with bypass toggles enabled, you may see warnings at startup.
To suppress only the warning logs (without changing bypass behavior), set:

```json
"Authorization": {
  "LogBypassWarnings": false
}
```

Keep this only for local/test scenarios.
