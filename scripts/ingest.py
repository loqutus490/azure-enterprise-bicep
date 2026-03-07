import os
import sys
import uuid
import json
import re
import hashlib
import unicodedata
import time
import functools
import logging
import datetime
import urllib.request
import urllib.error

from dotenv import load_dotenv
from openai import AzureOpenAI
from azure.identity import AzureCliCredential, DefaultAzureCredential, get_bearer_token_provider
from azure.search.documents import SearchClient
from azure.core.credentials import AzureKeyCredential
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
AZURE_OPENAI_EMBEDDING_DIMENSIONS = os.getenv("AZURE_OPENAI_EMBEDDING_DIMENSIONS")
AZURE_SEARCH_ENDPOINT = os.getenv("AZURE_SEARCH_ENDPOINT")
AZURE_SEARCH_INDEX = os.getenv("AZURE_SEARCH_INDEX")
AZURE_SEARCH_KEY = os.getenv("AZURE_SEARCH_KEY")
DOCUMENT_VERSION_DEFAULT = os.getenv("RAG_DOCUMENT_VERSION", "v1.0")
CREATE_NEW_INDEX_VERSION = os.getenv("RAG_CREATE_NEW_INDEX_VERSION", "true").lower() in {"1", "true", "yes"}

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

embedding_dimensions = None
if AZURE_OPENAI_EMBEDDING_DIMENSIONS:
    try:
        embedding_dimensions = int(AZURE_OPENAI_EMBEDDING_DIMENSIONS)
    except ValueError:
        print("❌ AZURE_OPENAI_EMBEDDING_DIMENSIONS must be an integer when provided.")
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
use_cli_credential = os.getenv("AZURE_USE_CLI_CREDENTIAL", "").lower() in {"1", "true", "yes"}
credential = AzureCliCredential() if use_cli_credential else DefaultAzureCredential()
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
    credential=AzureKeyCredential(AZURE_SEARCH_KEY) if AZURE_SEARCH_KEY else credential,
)
active_index_name = AZURE_SEARCH_INDEX
checksum_supported = True


def _api_call(method: str, url: str, body: dict | None = None) -> dict:
    if not AZURE_SEARCH_KEY:
        raise RuntimeError("AZURE_SEARCH_KEY is required for index management operations.")

    data = None
    if body is not None:
        data = json.dumps(body).encode("utf-8")

    req = urllib.request.Request(url=url, method=method.upper(), data=data)
    req.add_header("Content-Type", "application/json")
    req.add_header("api-key", AZURE_SEARCH_KEY)
    with urllib.request.urlopen(req, timeout=60) as response:
        payload = response.read().decode("utf-8")
        return json.loads(payload) if payload else {}


def create_versioned_index_if_needed(repo_root: str) -> str:
    global search_client
    global active_index_name

    if not CREATE_NEW_INDEX_VERSION:
        return active_index_name

    if not AZURE_SEARCH_KEY:
        logger.warning("Skipping index version creation because AZURE_SEARCH_KEY is not set.")
        return active_index_name

    timestamp = datetime.datetime.utcnow().strftime("%Y%m%d%H%M%S")
    new_index_name = f"{AZURE_SEARCH_INDEX}-v{timestamp}"
    api_version = "2024-07-01"

    try:
        base_index = _api_call(
            "GET",
            f"{AZURE_SEARCH_ENDPOINT}/indexes/{AZURE_SEARCH_INDEX}?api-version={api_version}",
        )
        base_index["name"] = new_index_name
        _api_call(
            "PUT",
            f"{AZURE_SEARCH_ENDPOINT}/indexes/{new_index_name}?api-version={api_version}",
            base_index,
        )
        logger.info("✅ Created new versioned index: %s", new_index_name)
    except Exception as exc:
        logger.warning("Could not create versioned index; using base index %s. Error: %s", AZURE_SEARCH_INDEX, exc)
        return active_index_name

    active_index_name = new_index_name
    search_client = SearchClient(
        endpoint=AZURE_SEARCH_ENDPOINT,
        index_name=active_index_name,
        credential=AzureKeyCredential(AZURE_SEARCH_KEY) if AZURE_SEARCH_KEY else credential,
    )

    # Update local active pointer shared with the API process.
    data_dir = os.path.join(repo_root, "src", "data")
    os.makedirs(data_dir, exist_ok=True)
    state_path = os.path.join(data_dir, "index-version-state.json")
    known = [active_index_name]
    if os.path.exists(state_path):
        try:
            with open(state_path, "r", encoding="utf-8") as existing:
                parsed = json.load(existing)
            known = parsed.get("KnownIndexes") or parsed.get("knownIndexes") or []
            if not isinstance(known, list):
                known = [active_index_name]
        except Exception:
            known = [active_index_name]

    if active_index_name not in known:
        known.append(active_index_name)

    with open(state_path, "w", encoding="utf-8") as f:
        json.dump(
            {
                "activeIndex": active_index_name,
                "knownIndexes": sorted(set(known)),
                "updatedAt": datetime.datetime.utcnow().replace(microsecond=0).isoformat() + "Z",
            },
            f,
            indent=2,
        )
    return active_index_name


def compute_checksum(raw_bytes: bytes) -> str:
    digest = hashlib.sha256(raw_bytes).hexdigest()
    return f"sha256-{digest}"


def is_duplicate_checksum_in_index(checksum: str) -> bool:
    escaped = checksum.replace("'", "''")
    results = search_client.search(
        search_text="*",
        filter=f"checksum eq '{escaped}'",
        top=1,
        select=["id"],
    )
    for _ in results:
        return True
    return False


def append_lineage_record(repo_root: str, record: dict) -> None:
    data_dir = os.path.join(repo_root, "src", "data")
    os.makedirs(data_dir, exist_ok=True)
    lineage_path = os.path.join(data_dir, "lineage-records.jsonl")
    with open(lineage_path, "a", encoding="utf-8") as f:
        f.write(json.dumps(record) + "\n")


def validate_openai_access() -> None:
    try:
        embedding_kwargs = {}
        if embedding_dimensions is not None:
            embedding_kwargs["dimensions"] = embedding_dimensions
        openai_client.embeddings.create(
            model=AZURE_OPENAI_EMBEDDING_DEPLOYMENT,
            input="ping",
            **embedding_kwargs,
        )
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
        logger.info("✅ Azure AI Search access validated for index '%s' (documents=%d).", active_index_name, count)
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
    embedding_kwargs = {}
    if embedding_dimensions is not None:
        embedding_kwargs["dimensions"] = embedding_dimensions
    return openai_client.embeddings.create(
        model=AZURE_OPENAI_EMBEDDING_DEPLOYMENT,
        input=input_data,
        **embedding_kwargs,
    )


@retry(max_attempts=3, initial_delay=1.0)
def upload_documents_with_retry(documents):
    return search_client.upload_documents(documents)


def load_document_metadata(documents_folder: str) -> dict:
    metadata_path = os.path.join(documents_folder, "metadata.json")
    if not os.path.exists(metadata_path):
        logger.warning("No metadata file found at %s. Create it to enforce matter-level ingestion.", metadata_path)
        return {}

    try:
        with open(metadata_path, "r", encoding="utf-8") as f:
            metadata = json.load(f)
    except Exception as e:
        logger.error("Failed to parse metadata file '%s': %s", metadata_path, e)
        return {}

    if not isinstance(metadata, dict):
        logger.error("Metadata file must be a JSON object keyed by filename.")
        return {}

    logger.info("Loaded metadata for %d document(s).", len(metadata))
    return metadata


def extract_matter_id_from_text(text: str) -> str:
    # Prefer explicit header-style declarations near the top of the document.
    head = text[:8000]
    labeled_patterns = [
        r"(?im)^\s*matter(?:\s+id|\s+number)?\s*[:#-]\s*([A-Za-z0-9][A-Za-z0-9._-]{1,63})\s*$",
        r"(?im)^\s*file\s*matter\s*[:#-]\s*([A-Za-z0-9][A-Za-z0-9._-]{1,63})\s*$",
    ]
    for pattern in labeled_patterns:
        match = re.search(pattern, head)
        if match:
            return match.group(1).strip()

    # Fallback: look for canonical MATTER-* token anywhere in the leading content.
    token_match = re.search(r"(?i)\b(MATTER-[A-Za-z0-9._-]{1,63})\b", head)
    if token_match:
        return token_match.group(1).strip()

    return ""


def extract_matter_id_from_filename(filename: str) -> str:
    match = re.search(r"(?i)\b(MATTER-[A-Za-z0-9._-]{1,63})\b", filename)
    if not match:
        return ""
    return match.group(1).strip()


def resolve_matter_id(filename: str, text: str, doc_metadata: dict) -> tuple[str, str]:
    metadata_matter_id = (doc_metadata.get("matterId") or "").strip() if isinstance(doc_metadata, dict) else ""
    if metadata_matter_id:
        return metadata_matter_id, "metadata"

    text_matter_id = extract_matter_id_from_text(text)
    if text_matter_id:
        return text_matter_id, "document-text"

    filename_matter_id = extract_matter_id_from_filename(filename)
    if filename_matter_id:
        return filename_matter_id, "filename"

    return "", ""


def extract_labeled_value(text: str, labels: list[str], max_chars: int = 8000) -> str:
    head = text[:max_chars]
    for label in labels:
        pattern = rf"(?im)^\s*{label}\s*[:#-]\s*(.+?)\s*$"
        match = re.search(pattern, head)
        if match:
            value = match.group(1).strip()
            if value:
                return value
    return ""


def normalize_confidentiality_level(value: str) -> str:
    if not value:
        return ""
    val = value.strip().lower()
    mapping = {
        "public": "Public",
        "internal": "Internal",
        "confidential": "Confidential",
        "restricted": "Restricted",
        "highly confidential": "Highly Confidential",
    }
    if val in mapping:
        return mapping[val]

    # Keyword-based fallback if the line includes extra words.
    if "highly confidential" in val:
        return "Highly Confidential"
    if "restricted" in val:
        return "Restricted"
    if "confidential" in val:
        return "Confidential"
    if "internal" in val:
        return "Internal"
    if "public" in val:
        return "Public"
    return value.strip()


def resolve_practice_area(text: str, doc_metadata: dict) -> tuple[str, str]:
    metadata_value = (doc_metadata.get("practiceArea") or "").strip() if isinstance(doc_metadata, dict) else ""
    if metadata_value:
        return metadata_value, "metadata"

    text_value = extract_labeled_value(text, ["Practice\\s*Area", "Area\\s*of\\s*Law"])
    if text_value:
        return text_value, "document-text"

    return "", ""


def resolve_client(text: str, doc_metadata: dict) -> tuple[str, str]:
    metadata_value = (doc_metadata.get("client") or "").strip() if isinstance(doc_metadata, dict) else ""
    if metadata_value:
        return metadata_value, "metadata"

    text_value = extract_labeled_value(text, ["Client", "Customer", "Represented\\s*Party"])
    if text_value:
        return text_value, "document-text"

    return "", ""


def resolve_confidentiality_level(text: str, doc_metadata: dict) -> tuple[str, str]:
    metadata_value = (doc_metadata.get("confidentialityLevel") or "").strip() if isinstance(doc_metadata, dict) else ""
    if metadata_value:
        return normalize_confidentiality_level(metadata_value), "metadata"

    text_value = extract_labeled_value(text, ["Confidentiality\\s*Level", "Confidentiality", "Classification"])
    if text_value:
        return normalize_confidentiality_level(text_value), "document-text"

    return "", ""


def ingest_documents() -> None:
    base_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(base_dir)
    documents_folder = os.path.join(repo_root, "documents")
    run_ingestion_timestamp = datetime.datetime.utcnow().replace(microsecond=0).isoformat() + "Z"
    global checksum_supported

    logger.info("📂 Documents folder: %s", documents_folder)

    if not os.path.exists(documents_folder):
        logger.error("Documents folder not found: %s", documents_folder)
        return

    files = os.listdir(documents_folder)
    logger.info("📄 Files found: %s", files)
    metadata_map = load_document_metadata(documents_folder)
    seen_checksums = set()

    documents_to_upload = []
    lineage_buffer = []

    for filename in files:
        if not filename.lower().endswith(".txt"):
            logger.warning("Skipping unsupported format: %s", filename)
            continue

        doc_metadata = metadata_map.get(filename, {})
        logger.info("🔍 Processing file: %s", filename)

        filepath = os.path.join(documents_folder, filename)
        try:
            with open(filepath, "rb") as f:
                raw = f.read()
        except Exception as exc:
            logger.warning("Skipping %s because file could not be read: %s", filename, exc)
            continue

        if not raw:
            logger.warning("Skipping %s because file is empty.", filename)
            continue

        checksum = compute_checksum(raw)
        if checksum in seen_checksums:
            logger.info("Skipping %s because duplicate checksum detected in this run (%s).", filename, checksum)
            continue
        seen_checksums.add(checksum)

        duplicate_in_index = False
        if checksum_supported:
            try:
                duplicate_in_index = is_duplicate_checksum_in_index(checksum)
            except Exception as exc:
                message = str(exc).lower()
                if "checksum" in message or "checksum" in repr(exc).lower():
                    checksum_supported = False
                    logger.warning("Checksum field missing in index; skipping further duplicate checks.")
                else:
                    logger.warning("Checksum duplicate check failed for %s: %s", filename, exc)
        if duplicate_in_index:
            logger.info("Skipping %s because duplicate checksum already exists in index (%s).", filename, checksum)
            continue

        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:
            logger.warning("Skipping %s because file appears corrupted or not valid UTF-8 text.", filename)
            continue
        text = clean_text(text)

        logger.info("🧪 Text length: %d", len(text))
        if not text:
            logger.warning("Empty after cleaning. Skipping %s", filename)
            continue

        matter_id, matter_id_source = resolve_matter_id(filename, text, doc_metadata)
        if not matter_id:
            logger.warning("Skipping %s because matterId was not found in metadata, document text, or filename.", filename)
            continue
        logger.info("🧾 matterId resolved for %s: %s (%s)", filename, matter_id, matter_id_source)
        practice_area, practice_area_source = resolve_practice_area(text, doc_metadata)
        client, client_source = resolve_client(text, doc_metadata)
        confidentiality_level, confidentiality_level_source = resolve_confidentiality_level(text, doc_metadata)
        if practice_area:
            logger.info("🧾 practiceArea resolved for %s: %s (%s)", filename, practice_area, practice_area_source)
        if client:
            logger.info("🧾 client resolved for %s: %s (%s)", filename, client, client_source)
        if confidentiality_level:
            logger.info(
                "🧾 confidentialityLevel resolved for %s: %s (%s)",
                filename,
                confidentiality_level,
                confidentiality_level_source,
            )

        chunks = chunk_text(text, max_chars=2000, overlap=200)
        logger.info("🧠 Creating embeddings for %d chunk(s)...", len(chunks))

        try:
            response = create_embeddings(chunks)
            embeddings = [item.embedding for item in response.data]
        except Exception as e:
            logger.error("Failed to create embeddings for %s: %s", filename, e)
            continue

        document_id = (doc_metadata.get("documentId") or filename).strip() if isinstance(doc_metadata, dict) else filename
        document_version = (doc_metadata.get("documentVersion") or DOCUMENT_VERSION_DEFAULT).strip() if isinstance(doc_metadata, dict) else DOCUMENT_VERSION_DEFAULT

        for idx, (chunk_text_val, emb) in enumerate(zip(chunks, embeddings)):
            documents_to_upload.append(
                {
                    "id": str(uuid.uuid4()),
                    "content": chunk_text_val,
                    "contentVector": emb,
                    "source": filename,
                    "sourceFile": filename,
                    "page": idx + 1,
                    "chunk_index": idx,
                    "matterId": matter_id,
                    "practiceArea": practice_area,
                    "client": client,
                    "confidentialityLevel": confidentiality_level,
                    "documentVersion": document_version,
                    "ingestionTimestamp": run_ingestion_timestamp,
                    "checksum": checksum,
                }
            )

        lineage_buffer.append(
            {
                "documentId": document_id,
                "sourceFile": filename,
                "checksum": checksum,
                "ingestionTimestamp": run_ingestion_timestamp,
                "indexedChunks": len(chunks),
                "indexVersion": active_index_name,
                "eventType": "ingest",
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

    for record in lineage_buffer:
        append_lineage_record(repo_root, record)


if __name__ == "__main__":
    repo_root_for_indexing = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    create_versioned_index_if_needed(repo_root_for_indexing)
    validate_openai_access()
    validate_search_access()
    ingest_documents()
