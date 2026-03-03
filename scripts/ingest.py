import os
import sys
import uuid
import unicodedata
import time
import functools
import logging

from dotenv import load_dotenv
from openai import AzureOpenAI
from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from azure.search.documents import SearchClient
from azure.core.exceptions import ClientAuthenticationError, HttpResponseError, ResourceNotFoundError

# ---------------------------
# Load environment variables
# ---------------------------
load_dotenv()

# Configure structured logging
logger = logging.getLogger("ingest")
logger.setLevel(logging.INFO)
if not logger.handlers:
    handler = logging.StreamHandler()
    formatter = logging.Formatter("%(asctime)s %(levelname)s %(message)s")
    handler.setFormatter(formatter)
    logger.addHandler(handler)


AZURE_OPENAI_ENDPOINT = os.getenv("AZURE_OPENAI_ENDPOINT")
AZURE_OPENAI_EMBEDDING_DEPLOYMENT = os.getenv("AZURE_OPENAI_EMBEDDING_DEPLOYMENT")
AZURE_SEARCH_ENDPOINT = os.getenv("AZURE_SEARCH_ENDPOINT")
AZURE_SEARCH_INDEX = os.getenv("AZURE_SEARCH_INDEX")

required_vars = {
    "AZURE_OPENAI_ENDPOINT": AZURE_OPENAI_ENDPOINT,
    "AZURE_OPENAI_EMBEDDING_DEPLOYMENT": AZURE_OPENAI_EMBEDDING_DEPLOYMENT,
    "AZURE_SEARCH_ENDPOINT": AZURE_SEARCH_ENDPOINT,
    "AZURE_SEARCH_INDEX": AZURE_SEARCH_INDEX,
}
missing = [name for name, val in required_vars.items() if not val]
if missing:
    print(f"❌ Missing required environment variables: {', '.join(missing)}")
    sys.exit(1)


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


# ---------------------------
# Create Azure clients via Entra ID (managed identity / workload identity)
# ---------------------------
credential = DefaultAzureCredential()
openai_token_provider = get_bearer_token_provider(
    credential,
    "https://cognitiveservices.azure.com/.default",
)

openai_client = AzureOpenAI(
    azure_endpoint=AZURE_OPENAI_ENDPOINT,
    azure_ad_token_provider=openai_token_provider,
    api_version="2024-12-01-preview",
)

search_client = SearchClient(
    endpoint=AZURE_SEARCH_ENDPOINT,
    index_name=AZURE_SEARCH_INDEX,
    credential=credential,
)


def validate_openai_access() -> None:
    try:
        openai_client.embeddings.create(model=AZURE_OPENAI_EMBEDDING_DEPLOYMENT, input="ping")
        logger.info("✅ Azure OpenAI access validated via Entra ID.")
    except ClientAuthenticationError as e:
        logger.error("❌ Azure OpenAI authentication failed.")
        logger.error("Ensure this identity has role 'Cognitive Services OpenAI User' on the OpenAI resource.")
        logger.error("Original error: %s", e)
        sys.exit(1)
    except HttpResponseError as e:
        logger.error("❌ Azure OpenAI request failed.")
        logger.error("Check endpoint/deployment name and role assignments.")
        logger.error("Original error: %s", e)
        sys.exit(1)


def validate_search_access() -> None:
    try:
        count = search_client.get_document_count()
        logger.info("✅ Azure AI Search access validated for index '%s' (documents=%d).", AZURE_SEARCH_INDEX, count)
    except ResourceNotFoundError as e:
        logger.error("❌ Search index '%s' not found.", AZURE_SEARCH_INDEX)
        logger.error("Create the index first (IaC or indexer setup) before ingestion.")
        logger.error("Original error: %s", e)
        sys.exit(1)
    except ClientAuthenticationError as e:
        logger.error("❌ Azure AI Search authentication failed.")
        logger.error("Ensure this identity has role 'Search Index Data Contributor' on the Search service.")
        logger.error("Original error: %s", e)
        sys.exit(1)
    except HttpResponseError as e:
        logger.error("❌ Azure AI Search request failed.")
        logger.error("Check endpoint/index name and role assignments.")
        logger.error("Original error: %s", e)
        sys.exit(1)


def clean_text(text: str) -> str:
    text = text.replace("\x00", "")
    text = unicodedata.normalize("NFKD", text)
    text = text.encode("utf-8", "ignore").decode("utf-8", "ignore")
    return text.strip()


def chunk_text(text: str, max_chars: int = 2000, overlap: int = 200) -> list[str]:
    if max_chars <= 0:
        return [text]

    chunks = []
    start = 0
    text_len = len(text)
    while start < text_len:
        end = start + max_chars
        chunks.append(text[start:end])
        if end >= text_len:
            break
        start = end - overlap if (end - overlap) > start else end

    return chunks


@retry(max_attempts=3, initial_delay=1.0)
def create_embeddings(input_data):
    return openai_client.embeddings.create(model=AZURE_OPENAI_EMBEDDING_DEPLOYMENT, input=input_data)


@retry(max_attempts=3, initial_delay=1.0)
def upload_documents_with_retry(documents):
    return search_client.upload_documents(documents)


def ingest_documents() -> None:
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
        if not filename.endswith(".txt"):
            continue

        logger.info("🔍 Processing file: %s", filename)

        filepath = os.path.join(documents_folder, filename)
        with open(filepath, "rb") as f:
            raw = f.read()

        text = raw.decode("utf-8", errors="ignore")
        text = clean_text(text)

        logger.info("🧪 Text length: %d", len(text))
        if not text:
            logger.warning("Empty after cleaning. Skipping %s", filename)
            continue

        chunks = chunk_text(text, max_chars=2000, overlap=200)
        logger.info("🧠 Creating embeddings for %d chunk(s)...", len(chunks))

        try:
            response = create_embeddings(chunks)
            embeddings = [item.embedding for item in response.data]
        except Exception as e:
            logger.error("Failed to create embeddings for %s: %s", filename, e)
            continue

        for idx, (chunk_text_val, emb) in enumerate(zip(chunks, embeddings)):
            documents_to_upload.append(
                {
                    "id": str(uuid.uuid4()),
                    "content": chunk_text_val,
                    "contentVector": emb,
                    "source": filename,
                    "chunk_index": idx,
                }
            )

    if not documents_to_upload:
        logger.warning("⚠️ Nothing to upload.")
        return

    logger.info("⬆️ Uploading to Azure Search in batches...")
    batch_size = 100
    total = len(documents_to_upload)
    for i in range(0, total, batch_size):
        batch = documents_to_upload[i : i + batch_size]
        try:
            upload_documents_with_retry(batch)
            logger.info("✅ Uploaded batch %d (%d documents)", i // batch_size + 1, len(batch))
        except Exception as e:
            logger.error("❌ Upload failed for batch starting at %d: %s", i, e)


if __name__ == "__main__":
    validate_openai_access()
    validate_search_access()
    ingest_documents()
