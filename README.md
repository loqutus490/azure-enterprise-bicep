# Legal RAG Platform

Secure internal AI assistant for law firms, built on Azure. Attorneys ask questions in a web chat or Microsoft Teams; the system retrieves relevant content from firm-approved documents (SharePoint/OneDrive) using RAG and returns grounded answers via Azure OpenAI.

## Enterprise Features

✔ Citation-based answers  
✔ Conversation memory  
✔ Role-based document access  
✔ Audit logging  
✔ Structured AI responses

## Architecture

```
                        ┌─────────────────────┐
                        │   Attorneys / Staff  │
                        └──────────┬──────────┘
                                   │
                    ┌──────────────┼──────────────┐
                    │              │               │
              ┌─────▼─────┐ ┌─────▼──────┐ ┌─────▼──────┐
              │  Web Chat  │ │   Teams    │ │   Slack    │
              │  (Custom)  │ │   Bot      │ │   Bot      │
              └─────┬──────┘ └─────┬──────┘ └─────┬──────┘
                    │              │               │
                    └──────────────┼───────────────┘
                                   │
                         ┌─────────▼─────────┐
                         │  Azure App Service │
                         │  (.NET 8 + Auth)   │
                         └────┬─────────┬─────┘
                              │         │
                    ┌─────────▼──┐  ┌───▼──────────┐
                    │ Azure AI   │  │ Azure OpenAI │
                    │ Search     │  │ (GPT-4o)     │
                    │ (RAG)      │  │              │
                    └─────┬──────┘  └──────────────┘
                          │
                    ┌─────▼──────┐
                    │ SharePoint │
                    │ / OneDrive │
                    └────────────┘
```

### Five Pieces of the Stack

| # | Component | Azure Service | Purpose |
|---|-----------|---------------|---------|
| 1 | **AI Brain** | Azure OpenAI (GPT-4o) | Generates answers from retrieved legal context |
| 2 | **Document Storage** | SharePoint / OneDrive | Stores firm-approved legal documents |
| 3 | **Search Layer** | Azure AI Search | Indexes documents, vector + keyword search (RAG) |
| 4 | **Access Control** | Microsoft Entra ID | Role-based access, audit trail, SSO |
| 5 | **Interface** | Web Chat + Teams Bot | How attorneys interact with the assistant |

### Supporting Services

- **Azure Key Vault** - Secrets management for non-code secrets with RBAC
- **Application Insights** - Monitoring, logging, audit trail
- **Azure VNet** - Network isolation with private endpoints (prod)
- **Budget Alerts** - Cost controls per environment

## Repository Structure

```
infra/
  main.bicep                    # Orchestrator - wires all modules
  modules/
    appservice.bicep            # App Service + Entra ID auth (EasyAuth v2)
    botservice.bicep            # Azure Bot Service (Teams + Web Chat)
    keyvault.bicep              # Key Vault (RBAC-based)
    monitoring.bicep            # Log Analytics + App Insights
    networking.bicep            # VNet, NSGs, private endpoints, DNS
    openai.bicep                # Azure OpenAI (GPT-4o + embeddings)
    search.bicep                # Azure AI Search + private endpoints
    storage.bicep               # Storage Account
    budget.bicep                # Cost management budgets
  params/
    dev.bicepparam              # Dev environment parameters
    prod.bicepparam             # Prod environment parameters
src/
  Program.cs                    # .NET 8 API (web chat + /ask endpoint)
  wwwroot/                      # Web chat frontend (HTML/CSS/JS)
scripts/
  setup-entra.sh                # Create Entra ID app registration
  setup-sharepoint-indexer.sh   # Configure SharePoint search indexer
  bootstrap.sh                  # Full environment bootstrap
  deploy.sh                     # App deployment
  full-deploy.sh                # Build + configure + deploy in one step
  tenant-deploy.sh              # Multi-tenant deployment
  run-local.sh                  # Run the app locally
  run-query.sh                  # Run interactive Python query tool via shared env adapter
  run-test-embedding.sh         # Run Python embedding connectivity test via shared env adapter
  ingest.py                     # Document ingestion (embeddings + upload)
  query.py                      # Interactive RAG query CLI
```

## Quick Start

### Prerequisites

- Azure CLI (`az`) installed and logged in
- .NET 8 SDK
- Python 3.8+ (for document ingestion)
- An Azure subscription with Owner/Contributor access

### Shared Environment Config (Recommended)

This repo now supports a canonical shared env file and adapters for each runtime:

```bash
cp .env.shared.example .env.shared
# Edit .env.shared with real endpoints, deployment names, and IDs
```

- .NET adapter: `scripts/adapters/dotnet-env.sh`
- Python adapter: `scripts/adapters/python-env.sh`
- Vite adapter generator: `agent13-frontend/scripts/generate-vite-env.sh`

### 1. Deploy Infrastructure

```bash
# Dev environment (no VNet, no auth)
az deployment group create \
  --resource-group <rg-name> \
  --template-file infra/main.bicep \
  --parameters infra/params/dev.bicepparam

# Prod environment (VNet + private endpoints enabled automatically)
az deployment group create \
  --resource-group <rg-name> \
  --template-file infra/main.bicep \
  --parameters infra/params/prod.bicepparam
```

### 2. Enable Entra ID Authentication

```bash
# Register the app in Entra ID (stores secret in Key Vault automatically)
./scripts/setup-entra.sh -n agent13-app-prod -v agent13-kv-prod

# Redeploy with the client ID from the output
az deployment group create \
  --resource-group <rg-name> \
  --template-file infra/main.bicep \
  --parameters environment=prod entraClientId='<client-id>'
```

### 3. Connect SharePoint Documents

```bash
# Configure the SharePoint indexer (uses Entra creds from step 2)
./scripts/setup-sharepoint-indexer.sh \
  -s agent13-search-prod \
  -k <search-admin-key> \
  -t contoso \
  -u /sites/LegalDocs \
  -a <entra-app-id> \
  -p <entra-app-secret> \
  -r
```

### 4. Ingest Documents (Production Pipeline)

The ingestion pipeline supports:
- Local folder source (`--source folder`, default)
- SharePoint source via Graph connector credentials (`--source sharepoint`)

It performs chunking, embedding generation via Azure OpenAI, batched upload to Azure AI Search, metadata enrichment, retry logic, and structured logging.

#### 4a. Local Folder Source

```bash
cp .env.shared.example .env.shared
# Edit .env.shared with your endpoints/deployments
# Copy documents/metadata.example.json to documents/metadata.json and
# set matterId/practiceArea/client/confidentialityLevel per file

python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
bash -lc 'source ./scripts/adapters/python-env.sh ./.env.shared && .venv/bin/python scripts/ingest.py --source folder --documents-path documents'
```

`scripts/ingest.py` now requires `documents/metadata.json` entries with `matterId` for each ingested file.
If Search RBAC/token auth is restricted in your environment, you can provide an admin key at runtime:

```bash
AZURE_SEARCH_KEY='<search-admin-key>' .venv/bin/python scripts/ingest.py
```

#### 4b. SharePoint Source

Set these variables in `.env.shared` first:
- `SHAREPOINT_TENANT_ID`
- `SHAREPOINT_CLIENT_ID`
- `SHAREPOINT_CLIENT_SECRET`
- `SHAREPOINT_DRIVE_ID`
- Optional: `SHAREPOINT_FOLDER_PATH`

Then run:

```bash
bash -lc 'source ./scripts/adapters/python-env.sh ./.env.shared && .venv/bin/python scripts/ingest.py --source sharepoint'
```

Notes:
- SharePoint ingestion currently processes UTF-8 text-like files (`.txt`, `.md`, `.csv`, `.json`).
- The pipeline requires a `matterId`; if not present in metadata/content/filename, set `RAG_DEFAULT_MATTER_ID` in `.env.shared`.

### 4b. Query + Embedding Checks (Python Utilities)

Use wrapper scripts so Python tools always consume variables from `.env.shared` through the adapter:

```bash
./scripts/run-test-embedding.sh ./.env.shared
./scripts/run-query.sh ./.env.shared
```

### 5. Deploy the Application

```bash
./scripts/deploy.sh
# Or use the full deployment script
./scripts/tenant-deploy.sh -t <tenant-id> -s <subscription-id> -e prod
```

If you deploy manually with `az webapp deploy`, package the publish output as a ZIP first.
Passing a folder path defaults to `--type static`, which fails for ASP.NET app content.

```bash
dotnet publish ./src/LegalRagApp.csproj -c Release -o ./publish
(cd publish && zip -r ../publish.zip .)

az webapp deploy \
  --resource-group <rg-name> \
  --name <webapp-name> \
  --src-path ./publish.zip \
  --type zip
```

### 5b. `/ask` request contract

`matterId` is required on every request to enforce matter-level retrieval filtering.
The caller token must also contain a permitted matter claim (configurable claim types under `Authorization:MatterIdClaimTypes`), otherwise the API returns `403 Forbidden`.

```json
{
  "question": "Summarize indemnification obligations.",
  "matterId": "MATTER-001",
  "practiceArea": "Corporate",
  "client": "Contoso",
  "confidentialityLevel": "Internal"
}
```

Development-only bypasses (do not use in production):
- `Authorization:BypassAuthInDevelopment`
- `Authorization:BypassMatterAuthorizationInDevelopment`

### 6. Enable Teams Bot

```bash
az deployment group create \
  --resource-group <rg-name> \
  --template-file infra/main.bicep \
  --parameters environment=prod \
    entraClientId='<app-client-id>' \
    botEntraAppId='<bot-app-client-id>'
```

### 7. GitHub Actions Auth (No Keys)

Workflows use Azure OIDC (federated identity), not `AZURE_CREDENTIALS` secrets.  
Configure these repository/environment variables in GitHub:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

## Security Features

- **Entra ID SSO** - Only authenticated firm members can access the assistant
- **Role-based access** - Controlled via Entra ID groups and App Service EasyAuth
- **Network isolation** - VNet with private endpoints in production
- **HTTPS only** - TLS 1.2 minimum, FTPS disabled
- **Key Vault** - All secrets stored centrally with RBAC
- **Audit trail** - Application Insights logs all queries and responses
- **Data residency** - Documents stay in your Azure tenant and SharePoint

## Environment Parameters

| Parameter | Description | Default |
|-----------|-------------|---------|
| `environment` | `dev` or `prod` | Required |
| `location` | Azure region | `westus3` |
| `namePrefix` | Resource name prefix | `agent13` |
| `entraClientId` | Entra app client ID (enables auth) | `''` (disabled) |
| `botEntraAppId` | Bot app client ID (enables Teams bot) | `''` (disabled) |
| `enableNetworking` | VNet + private endpoints | `true` in prod |
| `deployRoleAssignments` | Create OpenAI/Search RBAC assignments from IaC (requires `Microsoft.Authorization/roleAssignments/write`) | `true` |
