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

// Test OpenAI
app.MapGet("/test-openai", async () =>
{
    var chatClient = openAiClient.GetChatClient("gpt-4.1-mini");

    var response = await chatClient.CompleteChatAsync(
        "Say hello from private Azure OpenAI."
    );

    return Results.Ok(response.Value.Content[0].Text);
});

// Test Search
app.MapGet("/ask", async (string question) =>
{
    var chatClient = openAiClient.GetChatClient("gpt-4.1-mini");

    // 1. Search relevant docs
    var searchOptions = new SearchOptions
    {
        Size = 3
    };

    var searchResults = await searchClient.SearchAsync<SearchDocument>(question, searchOptions);

    var context = "";

    await foreach (var result in searchResults.Value.GetResultsAsync())
    {
        context += result.Document["content"] + "\n";
    }

    // 2. Send to OpenAI with context
    var prompt = $"""
    Use the following contract context to answer the question.

    Context:
    {context}

    Question:
    {question}
    """;

    var response = await chatClient.CompleteChatAsync(prompt);

    return Results.Ok(response.Value.Content[0].Text);
});
app.Run();