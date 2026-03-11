# Bicep Infrastructure Hardening Review

This repository now includes a hardened production deployment template at:

- `infra/main.hardened.bicep`

## What was hardened

1. **Environment separation**
   - `environment` in `infra/main.bicep` is now constrained to `dev` or `prod`.
   - Parameter files now explicitly set networking by environment:
     - `infra/params/dev.bicepparam`: `enableNetworking = false`
     - `infra/params/prod.bicepparam`: `enableNetworking = true`

2. **Secrets posture / Key Vault baseline**
   - Key Vault now enforces RBAC, soft delete retention (90 days), and purge protection.
   - Production networking path deploys a Key Vault private endpoint and private DNS zone binding.
   - App managed identity receives **Key Vault Secrets User** role assignment.

3. **Managed identity first**
   - App configuration advertises managed identity usage for Search/OpenAI.
   - OpenAI account has `disableLocalAuth: true` to remove API-key auth path.
   - Production parameters now enable role assignments to grant data-plane RBAC.

4. **Azure AI Search private network restriction**
   - Search `publicNetworkAccess` is disabled whenever private endpoint mode is enabled.
   - Hardened template forces production with private networking enabled.

## Deploy hardened production

```bash
az deployment group create \
  --resource-group <prod-rg> \
  --template-file infra/main.hardened.bicep \
  --parameters entraClientId='<entra-api-client-id>' \
               allowedClientApplications='["<approved-caller-app-id>"]' \
               budgetContactEmails='["security@contoso.com"]'
```

## Notes

- Keep `DebugRag` disabled in production unless explicitly approved.
- Keep caller allowlist (`allowedClientApplications`) tightly scoped.
