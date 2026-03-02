# Production Readiness Runbook

This runbook provides concrete, command-driven gates for production rollout of this repository.

## 1. Required Setup

Set these environment variables before running checks:

```bash
export RESOURCE_GROUP="<prod-rg>"
export APP_NAME="<prod-app-service-name>"
export SEARCH_SERVICE="<prod-search-service-name>"
export OPENAI_ACCOUNT="<prod-openai-account-name>"
export SUBSCRIPTION_ID="<subscription-id>"
```

Select the correct subscription:

```bash
az account set --subscription "$SUBSCRIPTION_ID"
```

Pass criteria:
- `az account show --query id -o tsv` equals `$SUBSCRIPTION_ID`

## 2. Local Build/Test Gate

```bash
dotnet restore
dotnet build ./src/LegalRagApp.csproj -c Release
dotnet test --nologo
```

Pass criteria:
- Build succeeds
- Tests pass with `Failed: 0`

## 3. Infrastructure Template Gate

Validate Bicep files:

```bash
az bicep build --file infra/main.bicep >/dev/null
az bicep build --file infra/modules/appservice.bicep >/dev/null
az bicep build --file infra/modules/openai.bicep >/dev/null
```

Pass criteria:
- All commands exit successfully

## 4. Production Parameter Gate

Confirm production caller allowlist is explicitly configured:

```bash
sed -n '1,200p' infra/params/prod.bicepparam
```

Pass criteria:
- `param entraClientId` is set to the intended production app registration
- `param allowedClientApplications` contains all approved calling app IDs

## 5. Dry-Run Deployment Gate

Run a what-if against production parameters:

```bash
az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  --template-file infra/main.bicep \
  --parameters infra/params/prod.bicepparam
```

Pass criteria:
- No unexpected deletes/replacements
- Planned changes match approved release scope

## 6. Production Deployment Gate

```bash
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file infra/main.bicep \
  --parameters infra/params/prod.bicepparam
```

Pass criteria:
- Provisioning state is `Succeeded`

## 7. App Auth Hardening Gate

Confirm EasyAuth and client-application restrictions are active:

```bash
az webapp auth show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME"
```

Pass criteria:
- Authentication required (`unauthenticatedClientAction` is 401 behavior)
- Azure AD auth enabled
- Allowed client applications include only approved app IDs

## 8. API Access Gate (Unauthenticated)

```bash
curl -i "https://${APP_NAME}.azurewebsites.net/ping"
curl -i -X POST "https://${APP_NAME}.azurewebsites.net/ask" \
  -H "Content-Type: application/json" \
  -d '{"question":"hello"}'
```

Pass criteria:
- `/ping` without token returns HTTP 401 in production (EasyAuth required globally)
- `/ask` without token returns HTTP 401 in production

## 9. API Access Gate (Authorized App)

Acquire a valid app token (from approved caller app) and test:

```bash
# TOKEN must be for API audience/client configured in AzureAd settings
export TOKEN="<app-access-token>"

curl -i -X POST "https://${APP_NAME}.azurewebsites.net/ask" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"question":"Summarize key obligations from contract1."}'
```

Pass criteria:
- Authorized caller receives HTTP 200
- Response body contains `answer`

## 10. Search/OpenAI Connectivity Gate

```bash
az search service show \
  --name "$SEARCH_SERVICE" \
  --resource-group "$RESOURCE_GROUP" \
  --query "{name:name,status:status,publicNetworkAccess:publicNetworkAccess}" \
  -o json

az cognitiveservices account show \
  --name "$OPENAI_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --query "{name:name,provisioningState:properties.provisioningState,publicNetworkAccess:properties.publicNetworkAccess}" \
  -o json
```

Pass criteria:
- Both resources are healthy/provisioned
- Production network posture matches policy (private endpoints / disabled public access as required)

## 11. Managed Identity + RBAC Gate

Confirm the app can use Entra identity for OpenAI/Search data-plane access:

```bash
APP_PRINCIPAL_ID="$(az webapp identity show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --query principalId -o tsv)"

OPENAI_ID="$(az cognitiveservices account show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$OPENAI_ACCOUNT" \
  --query id -o tsv)"

SEARCH_ID="$(az search service show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$SEARCH_SERVICE" \
  --query id -o tsv)"

az role assignment list --assignee-object-id "$APP_PRINCIPAL_ID" --scope "$OPENAI_ID" \
  --query "[].roleDefinitionName" -o tsv

az role assignment list --assignee-object-id "$APP_PRINCIPAL_ID" --scope "$SEARCH_ID" \
  --query "[].roleDefinitionName" -o tsv
```

Pass criteria:
- Includes `Cognitive Services OpenAI User` on the OpenAI scope
- Includes `Search Index Data Reader` on the Search scope

## 12. Keyless App Settings Gate

Confirm key-based secrets are not present in App Service settings:

```bash
az webapp config appsettings list \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --query "[?name=='AzureOpenAI__Key' || name=='AzureSearch__Key'].name" \
  -o tsv
```

Pass criteria:
- Output is empty

## 13. Observability Gate

Confirm App Insights wiring exists:

```bash
az webapp config appsettings list \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --query "[?name=='APPINSIGHTS_INSTRUMENTATIONKEY' || name=='APPLICATIONINSIGHTS_CONNECTION_STRING']" \
  -o table
```

Pass criteria:
- App Insights setting present
- Query traffic and `AskRequest completed` logs visible during test traffic

## 14. Rollout Decision

Promote to production only if all gates pass.

Minimum required status:
- `PASS`: Local build/tests
- `PASS`: Infra template validation + what-if review
- `PASS`: Auth enforcement + allowed caller restrictions
- `PASS`: End-to-end authorized `/ask` request
- `PASS`: Logging and monitoring visibility
