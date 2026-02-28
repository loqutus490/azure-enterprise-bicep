import os
import sys
import uuid
import unicodedata
import json
import requests
import time
import functools
import logging
from dotenv import load_dotenv
from openai import AzureOpenAI
from azure.search.documents import SearchClient
from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    SearchIndex,
    SimpleField,
    SearchableField,
    SearchFieldDataType,
    VectorSearch,
    VectorSearchAlgorithmConfiguration,
)
from azure.core.credentials import AzureKeyCredential

# ---------------------------
# Load environment variables
# ---------------------------
load_dotenv()

# Configure structured logging
logger = logging.getLogger("ingest")
logger.setLevel(logging.INFO)
handler = logging.StreamHandler()
formatter = logging.Formatter("%(asctime)s %(levelname)s %(message)s")
handler.setFormatter(formatter)
logger.addHandler(handler)


def retry(max_attempts: int = 3, initial_delay: float = 1.0, backoff: float = 2.0, exceptions=(Exception,)):
    """Simple retry decorator with exponential backoff."""
    def deco(func):
        @functools.wraps(func)
        def wrapper(*args, **kwargs):
            delay = initial_delay
            last_exc = None
            for attempt in range(1, max_attempts + 1):
                try:
                    return func(*args, **kwargs)
                except exceptions as e:
                    last_exc = e
                    if attempt == max_attempts:
                        logger.error("Giving up after %d attempts: %s", attempt, e)
                        raise
                    logger.warning("Attempt %d failed: %s — retrying in %.1fs", attempt, e, delay)
                    time.sleep(delay)
                    delay *= backoff
            raise last_exc
        return wrapper
    return deco

AZURE_OPENAI_KEY = os.getenv("AZURE_OPENAI_KEY")
AZURE_OPENAI_ENDPOINT = os.getenv("AZURE_OPENAI_ENDPOINT")
AZURE_SEARCH_ENDPOINT = os.getenv("AZURE_SEARCH_ENDPOINT")
AZURE_SEARCH_KEY = os.getenv("AZURE_SEARCH_KEY")
AZURE_SEARCH_INDEX = os.getenv("AZURE_SEARCH_INDEX")

# ---------------------------
# Validate required environment variables before creating clients
# ---------------------------
required_vars = {
    "AZURE_OPENAI_KEY": AZURE_OPENAI_KEY,
    "AZURE_OPENAI_ENDPOINT": AZURE_OPENAI_ENDPOINT,
    "AZURE_SEARCH_ENDPOINT": AZURE_SEARCH_ENDPOINT,
    "AZURE_SEARCH_KEY": AZURE_SEARCH_KEY,
    "AZURE_SEARCH_INDEX": AZURE_SEARCH_INDEX,
}
missing = [name for name, val in required_vars.items() if not val]
if missing:
    print(f"❌ Missing required environment variables: {', '.join(missing)}")
    sys.exit(1)


def _mask_secret(s: str, show_start: int = 4, show_end: int = 4) -> str:
    if not s:
        return ""
    if len(s) <= (show_start + show_end + 3):
        return "*" * len(s)
    return f"{s[:show_start]}...{s[-show_end:]}"


def _print_search_key_guidance(e: Exception):
    logger.error("🔒 Azure Search authentication failed. Guidance:")
    logger.error("- Check that AZURE_SEARCH_KEY is the Primary or Secondary admin key in the Azure portal.")
    logger.error("- Ensure AZURE_SEARCH_ENDPOINT matches your Search service endpoint (e.g. https://<name>.search.windows.net).")
    logger.error("- Confirm the AZURE_SEARCH_INDEX exists and the key has permission to write to the index.")
    logger.error("- If you recently rotated keys, update your environment or restart your shell.")
    logger.error("- You can regenerate keys in Azure Portal -> Your Search Service -> Keys.")
    try:
        logger.info("Endpoint: %s", _mask_secret(AZURE_SEARCH_ENDPOINT, show_start=8, show_end=0))
        logger.info("Key (masked): %s", _mask_secret(AZURE_SEARCH_KEY))
    except Exception:
        pass
    logger.error("Original error: %s", e)

# ---------------------------
# Create Azure OpenAI client
# ---------------------------
client = AzureOpenAI(
    api_key=AZURE_OPENAI_KEY,
    azure_endpoint=AZURE_OPENAI_ENDPOINT,
    api_version="2024-12-01-preview"
)

# ---------------------------
# Create Azure Search client
# ---------------------------
search_client = SearchClient(
    endpoint=AZURE_SEARCH_ENDPOINT,
    index_name=AZURE_SEARCH_INDEX,
    credential=AzureKeyCredential(AZURE_SEARCH_KEY),
)


def validate_search_credentials(client: SearchClient, index_name: str):
    """Quickly validate that the provided Azure Search credentials can access the index.

    Exits the process with guidance if validation fails.
    """
    try:
        # get_document_count is a lightweight call that requires valid auth and index access
        get_document_count(client)
        logger.info("✅ Azure Search credentials validated for index '%s'.", index_name)
    except Exception as e:
        # If the index is missing, attempt to create a minimal index automatically.
        err_text = str(e)
        if "was not found" in err_text or "not found" in err_text:
            logger.info("Index '%s' not found — attempting to create it via REST API...", index_name)
            # Build exact JSON index definition required by the Azure Search REST API
            index_def = {
                "name": index_name,
                "fields": [
                    {"name": "id", "type": "Edm.String", "key": True, "searchable": False},
                    {"name": "content", "type": "Edm.String", "searchable": True},
                    {"name": "source", "type": "Edm.String", "filterable": True},
                    {"name": "chunk_index", "type": "Edm.Int32", "filterable": True, "sortable": True},
                    {"name": "contentVector", "type": "Collection(Edm.Single)", "dimensions": 3072, "vectorSearchConfiguration": "vector-config"}
                ],
                "vectorSearch": {
                    "algorithmConfigurations": [
                        {"name": "vector-config", "kind": "hnsw"}
                    ]
                }
            }

            @retry(max_attempts=3, initial_delay=1.0)
            def create_index_via_rest(endpoint: str, api_key: str, index_payload: dict) -> bool:
                url = endpoint.rstrip("/") + "/indexes?api-version=2023-07-01-Preview"
                headers = {
                    "api-key": api_key,
                    "Content-Type": "application/json"
                }
                try:
                    resp = requests.post(url, headers=headers, data=json.dumps(index_payload))
                except Exception as req_err:
                    logger.error("Network error while creating index: %s", req_err)
                    raise

                if resp.status_code in (200, 201):
                    logger.info("✅ Successfully created index '%s' via REST API.", index_name)
                    return True
                else:
                    logger.error("REST API create-index failed (%s): %s", resp.status_code, resp.text)
                    return False

            created = create_index_via_rest(AZURE_SEARCH_ENDPOINT, AZURE_SEARCH_KEY, index_def)
            if created:
                logger.info("Index created — continuing ingestion.")
                return
            else:
                logger.error("Failed to create index '%s' via REST API.", index_name)
                _print_search_key_guidance(e)
                sys.exit(1)

        _print_search_key_guidance(e)
        sys.exit(1)


# Validate search credentials at startup and fail fast with guidance
def validate_openai_credentials(client: AzureOpenAI, model: str = "text-embedding-3-large"):
    """Quickly validate Azure OpenAI credentials by making a lightweight embedding call.

    Exits the process with guidance if validation fails.
    """
    try:
        create_embeddings(client, model, "ping")
        logger.info("✅ Azure OpenAI credentials validated.")
    except Exception as e:
        logger.error("🔒 Azure OpenAI authentication failed. Guidance:")
        logger.error("- Check that AZURE_OPENAI_KEY is set to a valid key for your Azure OpenAI resource.")
        logger.error("- Ensure AZURE_OPENAI_ENDPOINT is the correct endpoint for your resource.")
        logger.error("- If you use role-based access, confirm the configured credentials have appropriate permissions.")
        try:
            logger.info("Endpoint: %s", _mask_secret(AZURE_OPENAI_ENDPOINT, show_start=12, show_end=0))
            logger.info("Key (masked): %s", _mask_secret(AZURE_OPENAI_KEY))
        except Exception:
            pass
        logger.error("Original error: %s", e)
        sys.exit(1)

# Validate OpenAI credentials first, then Azure Search
validate_openai_credentials(client)
validate_search_credentials(search_client, AZURE_SEARCH_INDEX)

# ---------------------------
# Safe text cleaner
# ---------------------------
def clean_text(text: str) -> str:
    # Remove null bytes
    text = text.replace("\x00", "")

    # Normalize unicode
    text = unicodedata.normalize("NFKD", text)

    # Remove invalid utf-8 sequences
    text = text.encode("utf-8", "ignore").decode("utf-8", "ignore")

    return text.strip()


def chunk_text(text: str, max_chars: int = 2000, overlap: int = 200) -> list:
    """Split text into overlapping chunks of up to `max_chars` characters.

    Returns a list of chunk strings.
    """
    if max_chars <= 0:
        return [text]

    chunks = []
    start = 0
    text_len = len(text)
    while start < text_len:
        end = start + max_chars
        chunk = text[start:end]
        chunks.append(chunk)
        if end >= text_len:
            break
        start = end - overlap if (end - overlap) > start else end

    return chunks


# Retry-enabled helpers for external calls
@retry(max_attempts=3, initial_delay=1.0)
def create_embeddings(client: AzureOpenAI, model: str, input_data):
    return client.embeddings.create(model=model, input=input_data)


@retry(max_attempts=3, initial_delay=1.0)
def upload_documents_with_retry(client: SearchClient, documents):
    return client.upload_documents(documents)


@retry(max_attempts=3, initial_delay=1.0)
def get_document_count(client: SearchClient):
    return client.get_document_count()

# ---------------------------
# Ingest Documents
# ---------------------------
def ingest_documents():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(base_dir)
    documents_folder = os.path.join(repo_root, "documents")

    logger.info("📂 Documents folder: %s", documents_folder)

    if not os.path.exists(documents_folder):
        logger.error("Documents folder not found: %s", documents_folder)
        return

    files = os.listdir(documents_folder)
    logger.info("📄 Files found: %s", files)

    documents_to_upload = []

    for filename in files:
        if filename.endswith(".txt"):
            logger.info("🔍 Processing file: %s", filename)

            filepath = os.path.join(documents_folder, filename)

            # Read as binary to prevent encoding issues
            with open(filepath, "rb") as f:
                raw = f.read()

            # Decode safely
            text = raw.decode("utf-8", errors="ignore")
            text = clean_text(text)

            logger.info("🧪 Text length: %d", len(text))
            logger.debug("🧪 Preview: %s", repr(text[:100]))

            if not text:
                logger.warning("Empty after cleaning. Skipping %s", filename)
                continue
            # Chunk the text to avoid very large single inputs and to preserve
            # retrieval granularity. Overlap keeps some context between chunks.
            chunks = chunk_text(text, max_chars=2000, overlap=200)
            logger.info("🧠 Creating embeddings for %d chunk(s)...", len(chunks))

            try:
                # Use retry-enabled helper to create embeddings for a list of chunks.
                response = create_embeddings(client, "text-embedding-3-large", chunks)
                embeddings = [item.embedding for item in response.data]
            except Exception as e:
                logger.error("Failed to create embeddings for %s: %s", filename, e)
                continue

            for idx, (chunk_text_val, emb) in enumerate(zip(chunks, embeddings)):
                documents_to_upload.append({
                    "id": str(uuid.uuid4()),
                    "content": chunk_text_val,
                    "contentVector": emb,
                    "source": filename,
                    "chunk_index": idx,
                })

    if documents_to_upload:
        logger.info("\n⬆️ Uploading to Azure Search in batches...")
        batch_size = 100
        total = len(documents_to_upload)
        for i in range(0, total, batch_size):
            batch = documents_to_upload[i : i + batch_size]
            try:
                upload_documents_with_retry(search_client, batch)
                logger.info("✅ Uploaded batch %d (%d documents)", i // batch_size + 1, len(batch))
            except Exception as e:
                logger.error("❌ Upload failed for batch starting at %d: %s", i, e)
    else:
        print("\n⚠️ Nothing to upload.")

# ---------------------------
# Run
# ---------------------------
if __name__ == "__main__":
    ingest_documents()
