# Law Firm RAG Implementation Checklist

This checklist aligns the current repository with the target architecture for a secure legal RAG platform.

## Implemented in this repo

- Entra ID app-to-app API authorization with required `Api.Access` role.
- Optional caller allowlist at both API layer and EasyAuth layer.
- Hybrid retrieval path in API (`vector + keyword`) against Azure AI Search.
- Controlled-answer prompt policy in API (context-bound responses, no-answer fallback).
- Structured audit logging in `/ask` for query metadata and retrieval metadata.
- VNet + private endpoint support for Search.
- VNet + private endpoint support for OpenAI (with public access disabled when enabled).
- HTTPS-only settings and auth hardening for App Service EasyAuth.

## Still required for production rollout

- Set production `allowedClientApplications` in `infra/params/prod.bicepparam` to all approved caller app IDs.
- Ensure `Api.Access` app role assignment for every calling service principal.
- Deploy production infra with `infra/params/prod.bicepparam` into the intended production resource group.
- Configure Blob private endpoint + firewall-only access for approved document containers.
- Add matter-level metadata fields to index and ingestion (`matterId`, `practiceArea`, `client`, `confidentialityLevel`).
- Enforce matter-level filter in API requests before retrieval.
- Expand audit events to include matter-level access checks and denial outcomes.
- Set retention policy and immutable logs (where required by firm policy).
- Add office/VPN ingress restrictions if required by policy.

## Verification commands

- API build: `dotnet build ./src/LegalRagApp.csproj`
- API health: `curl -i https://<app-name>.azurewebsites.net/ping`
- Bicep diagnostics:
  - `infra/main.bicep`
  - `infra/modules/appservice.bicep`
  - `infra/modules/openai.bicep`

## Deployment note

Production deployment command:

`az deployment group create --resource-group <prod-rg> --template-file infra/main.bicep --parameters infra/params/prod.bicepparam`
