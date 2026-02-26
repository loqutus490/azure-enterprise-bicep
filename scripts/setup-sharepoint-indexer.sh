#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# setup-sharepoint-indexer.sh
# Configures Azure AI Search to index documents from SharePoint
# via the built-in SharePoint Online indexer connector.
# ============================================================

usage() {
    echo "Usage: $0 -s <search-service> -k <admin-key> -t <sharepoint-tenant> -u <site-url>"
    echo "          -a <entra-app-id> -p <entra-app-secret> [-d <tenant-id>] [-i <index-name>] [-r]"
    echo ""
    echo "Options:"
    echo "  -s  Azure Search service name (required)"
    echo "  -k  Azure Search admin key (required)"
    echo "  -t  SharePoint tenant name, e.g. 'contoso' (required)"
    echo "  -u  SharePoint site URL path, e.g. '/sites/LegalDocs' (required)"
    echo "  -a  Entra ID application (client) ID for SharePoint access (required)"
    echo "  -p  Entra ID application client secret (required)"
    echo "  -d  Azure AD tenant ID (default: auto-detected from az CLI)"
    echo "  -i  Index name (default: legal-documents)"
    echo "  -r  Run the indexer immediately after setup"
    echo ""
    echo "Prerequisites:"
    echo "  - Azure AI Search service (Basic tier or higher)"
    echo "  - SharePoint Online site with documents"
    echo "  - Entra ID app with Sites.Read.All permission granted for SharePoint"
    echo ""
    echo "Example:"
    echo "  $0 -s agent13-search-prod -k <key> -t contoso -u /sites/LegalDocs \\"
    echo "     -a <client-id> -p <client-secret> -r"
    exit 1
}

SEARCH_SERVICE=""
ADMIN_KEY=""
SP_TENANT=""
SP_SITE_URL=""
ENTRA_APP_ID=""
ENTRA_APP_SECRET=""
ENTRA_TENANT_ID=""
INDEX_NAME="legal-documents"
RUN_NOW=false

while getopts "s:k:t:u:a:p:d:i:rh" opt; do
    case $opt in
        s) SEARCH_SERVICE="$OPTARG" ;;
        k) ADMIN_KEY="$OPTARG" ;;
        t) SP_TENANT="$OPTARG" ;;
        u) SP_SITE_URL="$OPTARG" ;;
        a) ENTRA_APP_ID="$OPTARG" ;;
        p) ENTRA_APP_SECRET="$OPTARG" ;;
        d) ENTRA_TENANT_ID="$OPTARG" ;;
        i) INDEX_NAME="$OPTARG" ;;
        r) RUN_NOW=true ;;
        h) usage ;;
        *) usage ;;
    esac
done

if [ -z "$SEARCH_SERVICE" ] || [ -z "$ADMIN_KEY" ] || [ -z "$SP_TENANT" ] || [ -z "$SP_SITE_URL" ]; then
    echo "Error: Missing required parameters."
    usage
fi

if [ -z "$ENTRA_APP_ID" ] || [ -z "$ENTRA_APP_SECRET" ]; then
    echo "Error: Entra ID credentials are required (-a and -p flags)."
    echo "  These are needed to authenticate with SharePoint Online."
    echo "  Use setup-entra.sh to create an app registration first."
    exit 1
fi

# Auto-detect tenant ID from Azure CLI if not provided
if [ -z "$ENTRA_TENANT_ID" ]; then
    if command -v az &> /dev/null && az account show > /dev/null 2>&1; then
        ENTRA_TENANT_ID=$(az account show --query tenantId -o tsv)
    else
        echo "Error: Tenant ID not provided and could not be auto-detected."
        echo "  Provide it with -d <tenant-id> or log in with 'az login'."
        exit 1
    fi
fi

SEARCH_ENDPOINT="https://${SEARCH_SERVICE}.search.windows.net"
API_VERSION="2024-07-01"

echo "=== SharePoint Indexer Setup ==="
echo "Search Service: $SEARCH_SERVICE"
echo "SharePoint:     ${SP_TENANT}.sharepoint.com${SP_SITE_URL}"
echo "Entra App ID:   $ENTRA_APP_ID"
echo "Tenant ID:      $ENTRA_TENANT_ID"
echo "Index:          $INDEX_NAME"
echo ""

# Helper: make an API call and check for errors
api_call() {
    local method="$1"
    local url="$2"
    local data="${3:-}"
    local step_desc="$4"

    local response
    local http_code

    if [ -n "$data" ]; then
        response=$(curl -s -w "\n%{http_code}" -X "$method" "$url" \
            -H "Content-Type: application/json" \
            -H "api-key: ${ADMIN_KEY}" \
            -d "$data")
    else
        response=$(curl -s -w "\n%{http_code}" -X "$method" "$url" \
            -H "Content-Type: application/json" \
            -H "api-key: ${ADMIN_KEY}")
    fi

    http_code=$(echo "$response" | tail -1)
    local body
    body=$(echo "$response" | sed '$d')

    if [[ "$http_code" -ge 200 && "$http_code" -lt 300 ]]; then
        echo "  OK (HTTP $http_code)"
        return 0
    else
        echo "  FAILED (HTTP $http_code)"
        echo "  Response: $body"
        echo ""
        echo "Error during: $step_desc"
        exit 1
    fi
}

# Verify search service is reachable
echo "Verifying search service connectivity..."
api_call "GET" "${SEARCH_ENDPOINT}/servicestats?api-version=${API_VERSION}" "" \
    "connectivity check"

# Step 1: Create the search index
echo ""
echo "Step 1: Creating search index '${INDEX_NAME}'..."
INDEX_BODY=$(cat <<EOF
{
    "name": "${INDEX_NAME}",
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
}
EOF
)
api_call "PUT" "${SEARCH_ENDPOINT}/indexes/${INDEX_NAME}?api-version=${API_VERSION}" \
    "$INDEX_BODY" "creating search index"

# Step 2: Create the SharePoint data source with real credentials
echo ""
echo "Step 2: Creating SharePoint data source..."
CONNECTION_STRING="SharePointOnlineEndpoint=https://${SP_TENANT}.sharepoint.com${SP_SITE_URL};ApplicationId=${ENTRA_APP_ID};ApplicationSecret=${ENTRA_APP_SECRET};TenantId=${ENTRA_TENANT_ID}"

DATASOURCE_BODY=$(cat <<EOF
{
    "name": "sharepoint-legal",
    "type": "sharepoint",
    "credentials": {
        "connectionString": "${CONNECTION_STRING}"
    },
    "container": {
        "name": "defaultSiteLibrary"
    }
}
EOF
)
api_call "PUT" "${SEARCH_ENDPOINT}/datasources/sharepoint-legal?api-version=${API_VERSION}" \
    "$DATASOURCE_BODY" "creating SharePoint data source"

# Step 3: Create the indexer
echo ""
echo "Step 3: Creating SharePoint indexer..."
INDEXER_BODY=$(cat <<EOF
{
    "name": "sharepoint-indexer",
    "dataSourceName": "sharepoint-legal",
    "targetIndexName": "${INDEX_NAME}",
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
}
EOF
)
api_call "PUT" "${SEARCH_ENDPOINT}/indexers/sharepoint-indexer?api-version=${API_VERSION}" \
    "$INDEXER_BODY" "creating SharePoint indexer"

# Step 4: Optionally run the indexer immediately
if [ "$RUN_NOW" = true ]; then
    echo ""
    echo "Step 4: Running indexer now..."
    api_call "POST" "${SEARCH_ENDPOINT}/indexers/sharepoint-indexer/run?api-version=${API_VERSION}" \
        "" "triggering indexer run"
fi

echo ""
echo "=== Setup Complete ==="
echo ""
echo "The indexer is scheduled to run every 2 hours."
echo ""
echo "Check indexer status:"
echo "  curl -s '${SEARCH_ENDPOINT}/indexers/sharepoint-indexer/status?api-version=${API_VERSION}' \\"
echo "    -H 'api-key: <admin-key>' | python3 -m json.tool"
echo ""
echo "Run indexer manually:"
echo "  curl -X POST '${SEARCH_ENDPOINT}/indexers/sharepoint-indexer/run?api-version=${API_VERSION}' \\"
echo "    -H 'api-key: <admin-key>'"
