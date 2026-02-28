using Azure;
using Azure.Identity;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// 🔐 Add Entra authentication
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization();

var app = builder.Build();

// 🔐 Enable auth middleware
app.UseAuthentication();
app.UseAuthorization();

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

// Public health endpoint
app.MapGet("/ping", () => "pong");

// 🔐 Protected version endpoint
app.MapGet("/version", () => "SECURED_BUILD_V1")
   .RequireAuthorization();

// 🔐 Protected RAG endpoint
app.MapPost("/ask", async (AskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest("Question is required.");

    var question = request.Question;

    var chatClient = openAiClient.GetChatClient("gpt-4.1-mini");

    var searchResults =
        await searchClient.SearchAsync<SearchDocument>(question);

    var context = "";

    await foreach (var result in searchResults.Value.GetResultsAsync())
    {
        if (result.Document.ContainsKey("content"))
            context += result.Document["content"]?.ToString() + "\n";
    }

    if (string.IsNullOrWhiteSpace(context))
        return Results.Ok("No relevant documents found.");

    var response = await chatClient.CompleteChatAsync(context + "\n\nQuestion: " + question);

    return Results.Ok(response.Value.Content[0].Text);
})
.RequireAuthorization();

app.Run();

public record AskRequest(string Question);