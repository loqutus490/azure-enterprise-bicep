from openai import AzureOpenAI

client = AzureOpenAI(
    api_key="42b9fb3719704f068f7ae2d6d3401471",
    azure_endpoint="https://agent13-openai-dev.openai.azure.com//",
    api_version="2024-12-01-preview"
)

response = client.embeddings.create(
    model="text-embedding-3-small",
    input="Test embedding."
)

print(len(response.data[0].embedding))
