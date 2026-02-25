import os
from dotenv import load_dotenv
from openai import AzureOpenAI
from azure.search.documents import SearchClient
from azure.core.credentials import AzureKeyCredential

# ---------------------------
# Load environment variables
# ---------------------------
load_dotenv()

AZURE_OPENAI_ENDPOINT = os.getenv("AZURE_OPENAI_ENDPOINT")
AZURE_OPENAI_KEY = os.getenv("AZURE_OPENAI_KEY")
AZURE_OPENAI_EMBEDDING_DEPLOYMENT = os.getenv("AZURE_OPENAI_EMBEDDING_DEPLOYMENT")
AZURE_OPENAI_CHAT_DEPLOYMENT = os.getenv("AZURE_OPENAI_CHAT_DEPLOYMENT")

AZURE_SEARCH_ENDPOINT = os.getenv("AZURE_SEARCH_ENDPOINT")
AZURE_SEARCH_KEY = os.getenv("AZURE_SEARCH_KEY")
AZURE_SEARCH_INDEX = os.getenv("AZURE_SEARCH_INDEX")

# ---------------------------
# Create clients
# ---------------------------
openai_client = AzureOpenAI(
    api_key=AZURE_OPENAI_KEY,
    api_version="2024-02-01",
    azure_endpoint=AZURE_OPENAI_ENDPOINT,
)

search_client = SearchClient(
    endpoint=AZURE_SEARCH_ENDPOINT,
    index_name=AZURE_SEARCH_INDEX,
    credential=AzureKeyCredential(AZURE_SEARCH_KEY),
)

# ---------------------------
# Generate embedding
# ---------------------------
def generate_embedding(text):
    response = openai_client.embeddings.create(
        model=AZURE_OPENAI_EMBEDDING_DEPLOYMENT,
        input=[text],
    )
    return response.data[0].embedding

# ---------------------------
# Perform vector search
# ---------------------------
def search_documents(query, top_k=5):
    embedding = generate_embedding(query)

    results = search_client.search(
        search_text=None,
        vector_queries=[
            {
                "vector": embedding,
                "k": top_k,
                "fields": "contentVector"
            }
        ],
        select=["content", "source"],
    )

    documents = []
    for result in results:
        documents.append(result["content"])

    return documents

# ---------------------------
# Generate final GPT answer
# ---------------------------
def generate_answer(question, context_chunks):
    context = "\n\n".join(context_chunks)

    prompt = f"""
You are a legal AI assistant.
Use the context below to answer the question.
If the answer is not in the context, say you don't know.

Context:
{context}

Question:
{question}

Answer:
"""

    response = openai_client.chat.completions.create(
        model=AZURE_OPENAI_CHAT_DEPLOYMENT,
        messages=[
            {"role": "system", "content": "You are a helpful legal AI assistant."},
            {"role": "user", "content": prompt}
        ],
        temperature=0.2
    )

    return response.choices[0].message.content

# ---------------------------
# Full RAG pipeline
# ---------------------------
def ask_question(question):
    context_chunks = search_documents(question)
    answer = generate_answer(question, context_chunks)
    return answer

# ---------------------------
# Run interactively
# ---------------------------
if __name__ == "__main__":
    while True:
        question = input("\nAsk a question (or type 'exit'): ")
        if question.lower() == "exit":
            break
        answer = ask_question(question)
        print("\nAnswer:\n", answer)

