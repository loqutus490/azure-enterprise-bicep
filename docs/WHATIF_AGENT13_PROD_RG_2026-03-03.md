# Production What-If Report

- Date: 2026-03-03
- Subscription: `422dc851-76ef-46e2-8350-35dad7e38c54`
- Resource group: `agent13-prod-rg`
- Command:
  - `az deployment group what-if --resource-group agent13-prod-rg --template-file infra/main.bicep --parameters infra/params/prod.bicepparam`

## Result Summary

- Status: Success
- Resource changes:
  - `2` to create
  - `5` to modify
  - `16` no change
  - `3` ignored

## Planned Creates

1. OpenAI role assignment for app principal (`Cognitive Services OpenAI User` role scope under `agent13-openai-prod`).
2. Search role assignment for app principal (`Search Index Data Reader` role scope under `agent13-search-prod`).

## Planned Modifications

1. `Microsoft.Consumption/budgets/budget-prod`
   - Normalization of `timePeriod` date values.
2. `Microsoft.Insights/components/agent13-insights-prod`
   - Adds `Flow_Type` and `Request_Source` properties.
3. `Microsoft.Network/privateEndpoints/pe-agent13-openai-prod/privateDnsZoneGroups/default`
   - DNS zone config metadata refresh.
4. `Microsoft.Network/privateEndpoints/pe-agent13-search-prod/privateDnsZoneGroups/default`
   - DNS zone config metadata refresh.
5. `Microsoft.Search/searchServices/agent13-search-prod`
   - Shows drift on properties currently present (`disableLocalAuth`, `encryptionWithCmk.enforcement`, `networkRuleSet.bypass`).

## Diagnostics

- `NestedDeploymentShortCircuited` on nested deployment `app`.
- Impact: validation skips some nested resources when parameters cannot be fully evaluated during what-if (documented behavior).
- Reference: https://aka.ms/WhatIfEvalStopped

## Notes

- A separate run targeting `rg-legal-prod` failed because that resource group does not exist in the current subscription.
- This report reflects the successful run against `agent13-prod-rg`.
