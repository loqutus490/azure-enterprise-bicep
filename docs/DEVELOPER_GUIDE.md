# Legal RAG Platform – Developer Guide

This guide is for engineers working on the .NET legal RAG platform in this repository.

---

## 1) Architecture Diagram

```mermaid
flowchart LR
    U[Attorney / Internal User] --> FE[Web Chat / Teams]
    FE --> API[LegalRagApp API (.NET 8)]

    API --> AUTH[Entra ID / EasyAuth + API Policy]
    API --> RET[RetrievalService]
    API --> CHAT[ChatService]
    API --> AUDIT[AuditService + MetricsService]

    RET --> SEARCH[Azure AI Search]
    RET --> ACL[AuthorizationFilter]

    CHAT --> AOAI[Azure OpenAI]

    ING[Ingestion Pipeline\n scripts/ingest.py] --> SEARCH
    ING --> SP[SharePoint / Local documents]

    API -. config/secrets .-> KV[Azure Key Vault]

    SEARCH -. private endpoint (prod) .- NET[VNet + Private DNS]
    AOAI -. private endpoint (prod) .- NET
    KV -. private endpoint (prod) .- NET
```

### Core components
- **API**: `src/` ASP.NET Core app exposing `/ask`, `/debug/retrieval`, health/admin endpoints.
- **Retrieval**: `IRetrievalService`/`RetrievalService` fetches candidate chunks from Azure AI Search and applies authorization filters.
- **Authorization**: `IAuthorizationFilter`/`AuthorizationFilter` extracts claims, enforces matter/group-based access, and builds search filters.
- **Generation**: `IChatService`/`ChatService` calls Azure OpenAI with grounded prompts.
- **Observability**: audit + metrics middleware/services for structured logging and operational metrics.
- **Infrastructure**: Bicep modules under `infra/` with hardened production entrypoint `infra/main.hardened.bicep`.

---

## 2) Request Flow (`POST /ask`)

1. Client sends `AskRequestDto` (`question`, `matterId`, `conversationId`, optional metadata).
2. API auth policy validates caller (Entra ID scope/role + optional caller allowlist).
3. Prompt security middleware sanitizes/question-checks input.
4. Query rewrite service normalizes user question.
5. Retrieval service runs vector search against Azure AI Search.
6. Authorization filter trims chunks to authorized subset.
7. If no authorized chunks remain, API returns grounded fallback response.
8. If chunks exist, chat service generates structured answer with citations.
9. Provenance/confidence are enriched and returned in `AskResponseDto`.
10. Audit logging middleware records query metadata and metrics.

---

## 3) Authentication Model

### Runtime auth
- Primary model is **Entra ID** via JWT bearer + EasyAuth policy gates.
- API policy (`ApiAccessPolicy`) checks:
  - delegated scope (default `access_as_user`) for user flows, or
  - app role (default `Api.Access`) for app-to-app flows,
  - optional allowed client app IDs allowlist.

### Claims-based data authorization
- `AuthorizationFilter` extracts:
  - user identity/email/role,
  - permitted matters (`permittedMatters` and configured aliases),
  - groups (`groups`, `roles`, aliases).
- Retrieval authorization requires the chunk to have required metadata and pass claim checks.

### Development toggles
- Development-only bypass settings exist (`Authorization:BypassAuthInDevelopment`, `Authorization:BypassMatterAuthorizationInDevelopment`).
- Keep these disabled outside local debug environments.

---

## 4) Retrieval Pipeline

### High-level stages
1. **Matter authorization pre-check**: deny request if requested matter is not in user permitted matters.
2. **Search filter construction**:
   - matter/security clauses,
   - metadata presence clauses (`accessGroup`, `documentType`),
   - optional ACL clauses from identity/groups.
3. **Embedding generation**: user question embedding via Azure OpenAI embedding deployment.
4. **Hybrid retrieval**: vector search in Azure AI Search with configured top-K.
5. **Post-retrieval authorization filtering**: filter unauthorized/incomplete chunks.
6. **Fallback decision**:
   - `fallback_no_docs` if search returned no chunks,
   - `fallback_unauthorized` if chunks found but all filtered out.
7. **Prompt building**: only authorized chunks are used for grounded answer generation.

### Debugging retrieval
- `POST /debug/retrieval` returns compact retrieval diagnostics when enabled by `DebugRag:Enabled=true`.
- Use this to compare raw vs filtered counts and inspect fallback reason without exposing full documents.

---

## 5) Deployment Instructions

## 5.1 Prerequisites
- Azure CLI (`az`) authenticated to target subscription.
- .NET 8 SDK (or use `scripts/setup-dotnet.sh` in Ubuntu-like environments).
- Python 3.8+ for ingestion tooling.

## 5.2 Infrastructure deployment

### Development
```bash
az deployment group create \
  --resource-group <dev-rg> \
  --template-file infra/main.bicep \
  --parameters infra/params/dev.bicepparam
```

### Production (hardened)
Use hardened template for production-safe defaults.

```bash
az deployment group create \
  --resource-group <prod-rg> \
  --template-file infra/main.hardened.bicep \
  --parameters entraClientId='<entra-api-client-id>' \
               allowedClientApplications='["<approved-caller-app-id>"]' \
               budgetContactEmails='["security@contoso.com"]'
```

## 5.3 Application build/test
```bash
dotnet build legal-rag-platform.sln
dotnet test legal-rag-platform.sln
```

## 5.4 Document ingestion
### Local folder source
```bash
bash -lc 'source ./scripts/adapters/python-env.sh ./.env.shared && .venv/bin/python scripts/ingest.py --source folder --documents-path documents'
```

### SharePoint source
```bash
bash -lc 'source ./scripts/adapters/python-env.sh ./.env.shared && .venv/bin/python scripts/ingest.py --source sharepoint'
```

---

## 6) Troubleshooting Guide

### A) `dotnet: command not found`
- Run `./scripts/setup-dotnet.sh`.
- If install fails with proxy errors, allow outbound access to:
  - `archive.ubuntu.com`
  - `security.ubuntu.com`
  - `api.nuget.org`
- See `docs/DOTNET_SETUP.md`.

### B) `az: command not found` or unable to run Bicep validation
- Install Azure CLI and ensure `az account show` succeeds.
- Validate templates:
```bash
az bicep build --file infra/main.bicep
az bicep build --file infra/main.hardened.bicep
```

### C) Unauthorized `/ask` responses
- Confirm Entra token has required scope/role.
- Verify caller app ID is in allowlist if allowlist is configured.
- Confirm user claims include requested `matterId`.

### D) Retrieval returns no answer/fallback
- Check `RetrievedChunkCount` and diagnostics fallback reason.
- Use `/debug/retrieval` (when enabled) to inspect raw vs filtered counts.
- Confirm indexed docs have required metadata (`matterId`, `accessGroup`, `documentType`).

### E) No documents appearing in search results
- Verify ingestion succeeded and documents were uploaded.
- Confirm index schema supports expected fields.
- Validate private network connectivity to Search/OpenAI in production VNets.

### F) OpenAI/Search auth issues in production
- Confirm app managed identity exists.
- Confirm RBAC assignments are deployed (`deployRoleAssignments=true`).
- Confirm private endpoints and DNS resolution are correctly configured.

---

## 7) Recommended Developer Workflow

1. Sync env config (`.env.shared`) and adapter scripts.
2. Run `dotnet build` + `dotnet test` locally.
3. Add/update integration tests when touching retrieval/auth behavior.
4. For infra changes:
   - validate Bicep,
   - run `what-if` before production apply,
   - keep prod parameters secret-free and pipeline-driven.
5. Use diagnostics endpoint only temporarily and keep disabled in production by default.
