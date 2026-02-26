using System.Text;
using System.Text.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Serve the web chat frontend
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/ask", async (HttpContext context) =>
{
    var request = await JsonSerializer.DeserializeAsync<QuestionRequest>(
        context.Request.Body,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
    );

    if (request == null || string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest("Question is required.");

    // ============================
    // AZURE SEARCH (RAG Retrieval)
    // ============================

    var searchEndpoint = Environment.GetEnvironmentVariable("AzureSearch__Endpoint");
    var searchKey = Environment.GetEnvironmentVariable("AzureSearch__Key");
    var indexName = Environment.GetEnvironmentVariable("AzureSearch__Index");

    if (string.IsNullOrEmpty(searchEndpoint) ||
        string.IsNullOrEmpty(searchKey) ||
        string.IsNullOrEmpty(indexName))
        return Results.Problem("Azure Search configuration missing.");

    var searchClient = new SearchClient(
        new Uri(searchEndpoint),
        indexName,
        new AzureKeyCredential(searchKey)
    );

    var searchResults = await searchClient.SearchAsync<SearchDocument>(request.Question);

    var contextBuilder = new StringBuilder();

    await foreach (var result in searchResults.Value.GetResultsAsync())
    {
        if (result.Document.TryGetValue("content", out var content))
        {
            contextBuilder.AppendLine(content?.ToString());
        }
    }

    // ============================
    // AZURE OPENAI
    // ============================

    var openAiEndpoint = Environment.GetEnvironmentVariable("AzureOpenAI__Endpoint");
    var openAiKey = Environment.GetEnvironmentVariable("AzureOpenAI__Key");
    var deployment = Environment.GetEnvironmentVariable("AzureOpenAI__Deployment");

    if (string.IsNullOrEmpty(openAiEndpoint) ||
        string.IsNullOrEmpty(openAiKey) ||
        string.IsNullOrEmpty(deployment))
        return Results.Problem("Azure OpenAI configuration missing.");

    var prompt = $"""
You are a legal assistant. Use ONLY the context below to answer the question.

Context:
{contextBuilder}

Question:
{request.Question}

Answer:
""";

    var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.Add("api-key", openAiKey);

    var body = new
    {
        messages = new[]
        {
            new { role = "user", content = prompt }
        },
        temperature = 0.2,
        max_tokens = 800
    };

    var json = JsonSerializer.Serialize(body);

    var apiUrl = $"{openAiEndpoint.TrimEnd('/')}/" +
        $"openai/deployments/{deployment}/chat/completions?api-version=2024-06-01";

    var response = await httpClient.PostAsync(
        apiUrl,
        new StringContent(json, Encoding.UTF8, "application/json")
    );

    var responseString = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
        return Results.Problem($"OpenAI call failed: {responseString}");

    using var doc = JsonDocument.Parse(responseString);

    var answer = doc.RootElement
        .GetProperty("choices")[0]
        .GetProperty("message")
        .GetProperty("content")
        .GetString();

    return Results.Json(new { answer });
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

record QuestionRequest(string Question);