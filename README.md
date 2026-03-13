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

## Request Flow (/ask)

1. **Authentication + authorization**: API validates Entra token scope/role (`ApiAccessPolicy`) and matter claims.  
2. **Query security + rewrite**: prompt security middleware sanitizes input, then query rewrite service normalizes legal phrasing.  
3. **Retrieval pipeline** (`IRetrievalService`): vector/hybrid search in Azure AI Search retrieves chunks with metadata.  
4. **Document-level authorization** (`IAuthorizationFilter`): chunks are filtered by `matterId`, `accessGroup`, and required metadata before prompt assembly.  
5. **Prompt assembly** (`IPromptBuilder`): grounded context + conversation history are composed into strict JSON-only legal prompt instructions.  
6. **Generation** (`IChatService`): Azure OpenAI produces structured answer; insufficient context falls back to `Insufficient information in approved documents.`  
7. **Provenance + response shaping**: citations/source metadata are enriched and returned in `/ask` response (`answer`, `sources`, `sourceMetadata`).  
8. **Audit + metrics**: structured audit logs include correlation ID, user/claims summary, retrieval counts, source metadata summary, and final answer status.

## Security and Authorization Model

- **Matter-level access control**: requests must include `matterId`; user claims must include permitted matter values.
- **Group-level document filtering**: retrieved chunks are filtered against `accessGroup` claims before model context is built.
- **Grounded-only answering**: system prompt forbids unsupported legal conclusions when approved context is missing.
- **Sensitive data minimization**: diagnostics and audit logs include metadata/snippets, not full document bodies.
- **Secure defaults**: managed identity and environment-driven configuration are used for Azure Search/OpenAI connectivity (no hardcoded secrets).

## Retrieval Diagnostics Mode

- Endpoint: `POST /debug/retrieval`
- Gated by configuration: `DebugRag:Enabled=true` or `DEBUG_RAG=true`
- Intended for protected debugging in controlled environments.
- Response includes query, user claims summary, raw/filtered counts, source metadata, prompt context preview, and fallback reason.

Example config (local):

```bash
export DEBUG_RAG=true
```

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
  - If `dotnet` is missing in your runner/container, run `./scripts/preflight-env.sh` first, then use `./scripts/setup-dotnet.sh` and see `docs/DOTNET_SETUP.md`.
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

## Local Build and Test

```bash
dotnet build
dotnet test
```

Integration tests use fakes/stubs (no live Azure dependency required) via `WebApplicationFactory` and test auth handlers.

## Azure Enterprise Hardening Notes

- Bicep deployment config supports managed identity app settings and environment-specific toggles.
- Production templates are designed for private networking readiness (VNet/private endpoints modules) and secure transport defaults.
- Keep debug diagnostics disabled in production unless temporarily required for incident triage.

## Roadmap Ideas

- Entra group overage handling and graph-backed claim expansion.
- Per-document legal hold and retention policy enforcement in retrieval filters.
- Signed audit export pipeline for downstream compliance systems.

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

## Enterprise RAG request flow (`/ask`)

1. `AskController` validates auth and input.
2. `IQueryRewriteService` rewrites the user query.
3. `IRetrievalService` retrieves candidate chunks from Azure AI Search.
4. `IAuthorizationFilter` enforces document-level security (`matterId`, `accessGroup`, claims).
5. `IPromptBuilder` constructs a grounded prompt from authorized chunks only.
6. `IChatService` generates a structured response.
7. Response includes answer + citation/source metadata; fallback is used when context is insufficient.
8. Structured audit events are emitted for Azure Monitor / App Insights ingestion.

## Security and authorization model

- **Default auth**: Entra ID JWT validation (`ApiAccessPolicy`) with required delegated scope/app role.
- **Matter authorization**: request `matterId` must be in user permitted-matter claims.
- **Document authorization**: retrieval results are filtered by metadata (`matterId`, `accessGroup`) and claims (`groups`, identity).
- **Grounded fallback**: if context is missing/filtered, API returns:
  - `"Insufficient information in approved documents."`

## Retrieval diagnostics mode

Protected diagnostics endpoint:
- `POST /debug/retrieval`

Enable only when explicitly needed:
- `DebugRag:Enabled=true` (or env var `DEBUG_RAG=true`)

Diagnostics returns compact retrieval metadata only (query, claims summary, counts, source IDs/files, matter/document metadata, prompt preview, fallback reason), and intentionally avoids full document-body leakage.

## Local testing

```bash
# If dotnet is missing, bootstrap first
./scripts/setup-dotnet.sh

dotnet build legal-rag-platform.sln
dotnet test legal-rag-platform.sln
```

If SDK installation fails behind a proxy, follow `docs/DOTNET_SETUP.md` (allowlist + pre-baked image guidance).

New to this? Start with `docs/FIRST_STEPS_LOCAL_SETUP.md` for a beginner-friendly walkthrough of the **first step**.

Quick automation for newcomers:

```bash
./scripts/do-next-steps.sh
```

This runs setup, build/test (when `dotnet` is available), and branch cleanup dry-run (when `origin` is configured).

Integration tests use fake test doubles (retrieval/chat/auth identity simulation) and do not require live Azure OpenAI or Azure Search.

## Deployment hardening notes

- Hardened production template available at `infra/main.hardened.bicep` (forces `environment=prod`, private networking, managed-identity role assignments, and diagnostics disabled by default).
- Hardening review and deployment guidance: `docs/BICEP_HARDENING.md`.
- Key Vault is deployed with RBAC, purge protection, 90-day soft delete retention, and private endpoint support in production.
- App Service is configured to use managed identity for Azure OpenAI/Azure AI Search access (no API keys required).
- App Service uses **system-assigned managed identity** and RBAC assignments for Azure OpenAI/Search access.
- Infrastructure supports **private networking** and private endpoints in production mode.
- Storage is hardened with HTTPS-only + TLS1.2 minimum + blob public access disabled; production mode uses private endpoint access for Blob.
- `DebugRag` is wired as an explicit IaC/app setting toggle and defaults to `false`.
- Keep production and development parameters separated (`infra/params/dev.bicepparam`, `infra/params/prod.bicepparam`).

## Roadmap

- Entra ID group-overage handling and Graph-based group expansion.
- Per-document legal hold and retention policy enforcement.
- Signed answer provenance bundles for eDiscovery workflows.
- Policy-as-code authorization rules using externalized entitlement engine.


## Branch cleanup

Use `scripts/cleanup-branches.sh` to identify and optionally delete remote branches that are already merged into the base branch.

```bash
# Dry-run (default)
./scripts/cleanup-branches.sh --remote origin --base main

# Delete non-protected merged branches
./scripts/cleanup-branches.sh --remote origin --base main --apply
```

The script intentionally skips protected branch names (for example `main`, `master`, `develop`, `dev`, and `work`) and supports an optional `--merge` mode for exceptional cases.

