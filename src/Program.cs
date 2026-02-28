using Azure;
using Azure.Identity;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Managed Identity credential
var credential = new DefaultAzureCredential();

// Azure OpenAI
var openAiEndpoint = new Uri("https://agent13-openai-dev.openai.azure.com/");
var openAiClient = new AzureOpenAIClient(openAiEndpoint, credential);

// Azure Search
var searchEndpoint = new Uri("https://agent13-search-dev.search.windows.net");
var searchIndexName = "agent13-index";

var searchClient = new SearchClient(
    searchEndpoint,
    searchIndexName,
    credential
);

// Health check
app.MapGet("/ping", () => "pong");

// Test OpenAI
app.MapGet("/test-openai", async () =>
{
    try
    {
        var chatClient = openAiClient.GetChatClient("gpt-4.1-mini");

        var response = await chatClient.CompleteChatAsync(
            "Say hello from private Azure OpenAI."
        );

        return Results.Ok(response.Value.Content[0].Text);
    }
    catch (Exception ex)
    {
        return Results.Problem($"OpenAI error: {ex.Message}");
    }
});

// Production RAG endpoint (POST)
app.MapPost("/ask", async (AskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest("Question is required.");

    var question = request.Question;

    try
    {
        var chatClient = openAiClient.GetChatClient("gpt-4.1-mini");

        // Search relevant documents
        var searchOptions = new SearchOptions
        {
            Size = 3
        };

        var searchResults =
            await searchClient.SearchAsync<SearchDocument>(question, searchOptions);

        var context = "";

        await foreach (var result in searchResults.Value.GetResultsAsync())
        {
            if (result.Document.ContainsKey("content"))
            {
                context += result.Document["content"]?.ToString() + "\n";
            }
        }

        if (string.IsNullOrWhiteSpace(context))
        {
            return Results.Ok("No relevant documents found.");
        }

        var prompt = $"""
        You are a legal assistant.

        Use ONLY the provided contract context to answer the question.
        If the answer is not contained in the context, say:
        "The answer was not found in the provided documents."

        Context:
        {context}

        Question:
        {question}
        """;

        var response = await chatClient.CompleteChatAsync(prompt);

        return Results.Ok(response.Value.Content[0].Text);
    }
    catch (Exception ex)
    {
        return Results.Problem($"RAG processing error: {ex.Message}");
    }
});

app.Run();

// Request model
public record AskRequest(string Question);