# Legal RAG Platform

Secure internal AI assistant for law firms, built on Azure. Attorneys ask questions in a web chat or Microsoft Teams; the system retrieves relevant content from firm-approved documents (SharePoint/OneDrive) using RAG and returns grounded answers via Azure OpenAI.

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

- **Azure Key Vault** - Secrets management (API keys, connection strings)
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
  tenant-deploy.sh              # Multi-tenant deployment
```

## Quick Start

### Prerequisites

- Azure CLI (`az`) installed and logged in
- .NET 8 SDK
- Python 3.8+ (for document ingestion)
- An Azure subscription with Owner/Contributor access

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
# Register the app in Entra ID
./scripts/setup-entra.sh -n agent13-app-prod

# Redeploy with the client ID from the output
az deployment group create \
  --resource-group <rg-name> \
  --template-file infra/main.bicep \
  --parameters environment=prod entraClientId='<client-id>'
```

### 3. Connect SharePoint Documents

```bash
# Configure the SharePoint indexer
./scripts/setup-sharepoint-indexer.sh \
  -s agent13-search-prod \
  -k <search-admin-key> \
  -t contoso \
  -u /sites/LegalDocs
```

### 4. Ingest Documents (Manual Alternative)

For `.txt` files outside of SharePoint:

```bash
cp local.env.example local.env
# Edit local.env with your keys

pip install -r requirements.txt
python3 ingest.py
```

### 5. Deploy the Application

```bash
./deploy.sh
# Or use the full deployment script
./tenant-deploy.sh -t <tenant-id> -s <subscription-id> -e prod
```

### 6. Enable Teams Bot

```bash
az deployment group create \
  --resource-group <rg-name> \
  --template-file infra/main.bicep \
  --parameters environment=prod \
    entraClientId='<app-client-id>' \
    botEntraAppId='<bot-app-client-id>'
```

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

