#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# setup-sharepoint-indexer.sh
# Configures Azure AI Search to index documents from SharePoint
# via the built-in SharePoint Online indexer connector.
# ============================================================

usage() {
    echo "Usage: $0 -s <search-service> -k <admin-key> -t <sharepoint-tenant> -u <site-url>"
    echo ""
    echo "Options:"
    echo "  -s  Azure Search service name (required)"
    echo "  -k  Azure Search admin key (required)"
    echo "  -t  SharePoint tenant name, e.g. 'contoso' (required)"
    echo "  -u  SharePoint site URL path, e.g. '/sites/LegalDocs' (required)"
    echo "  -i  Index name (default: legal-documents)"
    echo ""
    echo "Prerequisites:"
    echo "  - Azure AI Search service (Basic tier or higher)"
    echo "  - SharePoint Online site with documents"
    echo "  - Entra ID app with Sites.Read.All permission for SharePoint access"
    echo ""
    echo "Example:"
    echo "  $0 -s agent13-search-prod -k <key> -t contoso -u /sites/LegalDocs"
    exit 1
}

SEARCH_SERVICE=""
ADMIN_KEY=""
SP_TENANT=""
SP_SITE_URL=""
INDEX_NAME="legal-documents"

while getopts "s:k:t:u:i:h" opt; do
    case $opt in
        s) SEARCH_SERVICE="$OPTARG" ;;
        k) ADMIN_KEY="$OPTARG" ;;
        t) SP_TENANT="$OPTARG" ;;
        u) SP_SITE_URL="$OPTARG" ;;
        i) INDEX_NAME="$OPTARG" ;;
        h) usage ;;
        *) usage ;;
    esac
done

if [ -z "$SEARCH_SERVICE" ] || [ -z "$ADMIN_KEY" ] || [ -z "$SP_TENANT" ] || [ -z "$SP_SITE_URL" ]; then
    echo "Error: Missing required parameters."
    usage
fi

SEARCH_ENDPOINT="https://${SEARCH_SERVICE}.search.windows.net"
API_VERSION="2024-07-01"

echo "=== SharePoint Indexer Setup ==="
echo "Search Service: $SEARCH_SERVICE"
echo "SharePoint:     ${SP_TENANT}.sharepoint.com${SP_SITE_URL}"
echo "Index:          $INDEX_NAME"
echo ""

# Step 1: Create the search index with fields for SharePoint documents
echo "Step 1: Creating search index '${INDEX_NAME}'..."
curl -s -X PUT "${SEARCH_ENDPOINT}/indexes/${INDEX_NAME}?api-version=${API_VERSION}" \
    -H "Content-Type: application/json" \
    -H "api-key: ${ADMIN_KEY}" \
    -d '{
    "name": "'"${INDEX_NAME}"'",
    "fields": [
        {"name": "id", "type": "Edm.String", "key": true, "filterable": true},
        {"name": "content", "type": "Edm.String", "searchable": true, "analyzer": "en.microsoft"},
        {"name": "metadata_spo_item_name", "type": "Edm.String", "searchable": true, "filterable": true, "sortable": true},
        {"name": "metadata_spo_item_path", "type": "Edm.String", "filterable": true},
        {"name": "metadata_spo_item_content_type", "type": "Edm.String", "filterable": true, "facetable": true},
        {"name": "metadata_spo_item_last_modified", "type": "Edm.DateTimeOffset", "filterable": true, "sortable": true},
        {"name": "metadata_spo_item_size", "type": "Edm.Int64", "filterable": true, "sortable": true},
        {"name": "metadata_spo_site_library", "type": "Edm.String", "filterable": true, "facetable": true}
    ],
    "semantic": {
        "configurations": [
            {
                "name": "legal-semantic",
                "prioritizedFields": {
                    "contentFields": [{"fieldName": "content"}],
                    "titleField": {"fieldName": "metadata_spo_item_name"}
                }
            }
        ]
    }
}'

echo ""
echo "Index created."

# Step 2: Create the SharePoint data source
echo ""
echo "Step 2: Creating SharePoint data source..."
curl -s -X PUT "${SEARCH_ENDPOINT}/datasources/sharepoint-legal?api-version=${API_VERSION}" \
    -H "Content-Type: application/json" \
    -H "api-key: ${ADMIN_KEY}" \
    -d '{
    "name": "sharepoint-legal",
    "type": "sharepoint",
    "credentials": {
        "connectionString": "SharePointOnlineEndpoint=https://'"${SP_TENANT}"'.sharepoint.com'"${SP_SITE_URL}"';ApplicationId=<ENTRA_APP_ID>;ApplicationSecret=<ENTRA_APP_SECRET>;TenantId=<TENANT_ID>"
    },
    "container": {
        "name": "defaultSiteLibrary"
    }
}'

echo ""
echo "Data source created."

# Step 3: Create the indexer
echo ""
echo "Step 3: Creating SharePoint indexer..."
curl -s -X PUT "${SEARCH_ENDPOINT}/indexers/sharepoint-indexer?api-version=${API_VERSION}" \
    -H "Content-Type: application/json" \
    -H "api-key: ${ADMIN_KEY}" \
    -d '{
    "name": "sharepoint-indexer",
    "dataSourceName": "sharepoint-legal",
    "targetIndexName": "'"${INDEX_NAME}"'",
    "parameters": {
        "configuration": {
            "indexedFileNameExtensions": ".pdf,.docx,.doc,.txt,.pptx,.xlsx",
            "excludedFileNameExtensions": ".png,.jpg,.gif,.zip,.exe",
            "dataToExtract": "contentAndMetadata"
        }
    },
    "schedule": {
        "interval": "PT2H"
    }
}'

echo ""
echo ""
echo "=== Setup Complete ==="
echo ""
echo "IMPORTANT: Before running the indexer, update the data source connection string"
echo "with your actual Entra ID credentials:"
echo ""
echo "  ApplicationId  = Your Entra app registration client ID"
echo "  ApplicationSecret = Your Entra app client secret"
echo "  TenantId       = Your Azure AD tenant ID"
echo ""
echo "The indexer is scheduled to run every 2 hours."
echo "To run immediately:"
echo "  curl -X POST '${SEARCH_ENDPOINT}/indexers/sharepoint-indexer/run?api-version=${API_VERSION}' \\"
echo "    -H 'api-key: ${ADMIN_KEY}'"
