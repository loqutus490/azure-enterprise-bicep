# legal-rag-platform

ingest.py ingests `.txt` documents into Azure Cognitive Search using Azure OpenAI embeddings.

Prerequisites

- Python 3.8+
- An Azure OpenAI resource and key
- An Azure Cognitive Search service and admin key

Setup

1. Copy your keys into `.env` with these variables:

```
AZURE_OPENAI_ENDPOINT=https://<your-openai-endpoint>
AZURE_OPENAI_KEY=<your-openai-key>
AZURE_SEARCH_ENDPOINT=https://<your-search-endpoint>
AZURE_SEARCH_KEY=<your-search-key>
AZURE_SEARCH_INDEX=<index-name>
```

2. Install dependencies:

```bash
python3 -m pip install -r requirements.txt
```

Usage

- Place `.txt` files into the `documents/` folder.
- Run ingestion:

```bash
python3 ingest.py
```

What the script does

- Validates Azure OpenAI and Azure Search credentials at startup.
- Attempts to create the Azure Search index (with vector search) if missing.
- Chunks large documents, creates embeddings per chunk, and uploads documents in batches.
- Provides retry/backoff and structured logging.

Notes

- The script assumes `text-embedding-3-small` embeddings (dimension 1536). Change in the code if you use a different model.
- For production use, consider adding monitoring, secrets management, and stronger error handling.

