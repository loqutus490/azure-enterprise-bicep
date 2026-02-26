import os
from dotenv import load_dotenv
from openai import AzureOpenAI

load_dotenv()

client = AzureOpenAI(
    api_key=os.environ["AZURE_OPENAI_KEY"],
    azure_endpoint=os.environ["AZURE_OPENAI_ENDPOINT"],
    api_version="2024-12-01-preview"
)

response = client.embeddings.create(
    model="text-embedding-3-large",
    input="Test embedding."
)

print(f"Embedding dimensions: {len(response.data[0].embedding)}")
