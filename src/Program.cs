using Azure;
using Azure.Identity;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using System.Diagnostics;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// 🔐 Add Entra authentication
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

var requiredApiRole = builder.Configuration["Authorization:RequiredRole"] ?? "Api.Access";
var allowedClientAppIds = builder.Configuration.GetSection("Authorization:AllowedClientAppIds").Get<string[]>() ?? Array.Empty<string>();
var allowedClientAppIdSet = new HashSet<string>(
    allowedClientAppIds.Where(clientAppId => !string.IsNullOrWhiteSpace(clientAppId)),
    StringComparer.OrdinalIgnoreCase);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiAccessPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole(requiredApiRole);
        policy.RequireAssertion(context =>
        {
            var user = context.User;
            var appId = user.FindFirst("azp")?.Value ?? user.FindFirst("appid")?.Value;

            if (string.IsNullOrWhiteSpace(appId))
                return false;

            if (user.HasClaim(claim => claim.Type == "scp"))
                return false;

            var idType = user.FindFirst("idtyp")?.Value;
            if (!string.IsNullOrWhiteSpace(idType) && !string.Equals(idType, "app", StringComparison.OrdinalIgnoreCase))
                return false;

            if (allowedClientAppIdSet.Count == 0)
                return true;

            return allowedClientAppIdSet.Contains(appId);
        });
    });
});

var app = builder.Build();
var logger = app.Logger;
var bypassAuthInDevelopment = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("Authorization:BypassAuthInDevelopment");

if (bypassAuthInDevelopment)
{
    logger.LogWarning("Authorization bypass is enabled for Development. Do not enable this outside local debugging.");
}

// 🔐 Enable auth middleware
app.UseAuthentication();
app.UseAuthorization();

// Managed Identity credential
var credential = new DefaultAzureCredential();

var openAiEndpointValue = builder.Configuration["AzureOpenAI:Endpoint"];
var chatDeploymentName = builder.Configuration["AzureOpenAI:Deployment"];
var embeddingDeploymentName = builder.Configuration["AzureOpenAI:EmbeddingDeployment"];
var openAiApiKey = builder.Configuration["AzureOpenAI:Key"];
var searchEndpointValue = builder.Configuration["AzureSearch:Endpoint"];
var searchIndexName = builder.Configuration["AzureSearch:Index"];
var searchApiKey = builder.Configuration["AzureSearch:Key"];

if (string.IsNullOrWhiteSpace(openAiEndpointValue))
    throw new InvalidOperationException("Missing configuration: AzureOpenAI:Endpoint");

if (string.IsNullOrWhiteSpace(chatDeploymentName))
    throw new InvalidOperationException("Missing configuration: AzureOpenAI:Deployment");

if (string.IsNullOrWhiteSpace(embeddingDeploymentName))
    throw new InvalidOperationException("Missing configuration: AzureOpenAI:EmbeddingDeployment");

if (string.IsNullOrWhiteSpace(searchEndpointValue))
    throw new InvalidOperationException("Missing configuration: AzureSearch:Endpoint");

if (string.IsNullOrWhiteSpace(searchIndexName))
    throw new InvalidOperationException("Missing configuration: AzureSearch:Index");

// Azure OpenAI
var openAiEndpoint = new Uri(openAiEndpointValue);
AzureOpenAIClient openAiClient = string.IsNullOrWhiteSpace(openAiApiKey)
    ? new AzureOpenAIClient(openAiEndpoint, credential)
    : new AzureOpenAIClient(openAiEndpoint, new AzureKeyCredential(openAiApiKey));

// Azure Search
var searchEndpoint = new Uri(searchEndpointValue);
SearchClient searchClient;
if (string.IsNullOrWhiteSpace(searchApiKey))
{
    searchClient = new SearchClient(searchEndpoint, searchIndexName, credential);
}
else
{
    searchClient = new SearchClient(searchEndpoint, searchIndexName, new AzureKeyCredential(searchApiKey));
}

// Public health endpoint
app.MapGet("/ping", () => "pong");

// 🔐 Protected version endpoint
var versionEndpoint = app.MapGet("/version", () => "SECURED_BUILD_V1");
if (!bypassAuthInDevelopment)
{
    versionEndpoint.RequireAuthorization("ApiAccessPolicy");
}

// 🔐 Protected RAG endpoint
var askEndpoint = app.MapPost("/ask", async (AskRequest request) =>
{
    var stopwatch = Stopwatch.StartNew();

    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest("Question is required.");

    var question = request.Question;

    var chatClient = openAiClient.GetChatClient(chatDeploymentName);
    var embeddingClient = openAiClient.GetEmbeddingClient(embeddingDeploymentName);

    var embeddingResponse = await embeddingClient.GenerateEmbeddingAsync(question);
    var questionVector = embeddingResponse.Value.ToFloats().ToArray();

    var searchOptions = new SearchOptions
    {
        Size = 5,
        VectorSearch = new VectorSearchOptions
        {
            Queries =
            {
                new VectorizedQuery(questionVector)
                {
                    KNearestNeighborsCount = 5,
                    Fields = { "contentVector" }
                }
            }
        }
    };
    searchOptions.Select.Add("content");

    var searchResults =
        await searchClient.SearchAsync<SearchDocument>(question, searchOptions);

    var context = "";
    var retrievedFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var retrievedChunkCount = 0;

    await foreach (var result in searchResults.Value.GetResultsAsync())
    {
        retrievedChunkCount++;

        if (result.Document.ContainsKey("filename"))
        {
            var filename = result.Document["filename"]?.ToString();
            if (!string.IsNullOrWhiteSpace(filename))
                retrievedFilenames.Add(filename);
        }

        if (result.Document.ContainsKey("content"))
            context += result.Document["content"]?.ToString() + "\n";
    }

    if (string.IsNullOrWhiteSpace(context))
    {
        logger.LogInformation("AskRequest completed with no retrieval results. QuestionLength={QuestionLength} RetrievedChunkCount={RetrievedChunkCount} DurationMs={DurationMs}",
            question.Length,
            retrievedChunkCount,
            stopwatch.ElapsedMilliseconds);

        return Results.Ok(new { answer = "No relevant documents found." });
    }

    var controlledPrompt = $"""
You are a legal AI assistant for internal law firm use.
Rules:
- Answer using only the provided context.
- If context is insufficient, reply exactly: "Insufficient information in approved documents."
- Do not provide legal advice beyond the source context.
- Be concise, factual, and cite filenames when available.

Context:
{context}

Question:
{question}
""";

    var response = await chatClient.CompleteChatAsync(controlledPrompt);

    logger.LogInformation("AskRequest completed. QuestionLength={QuestionLength} RetrievedChunkCount={RetrievedChunkCount} RetrievedFiles={RetrievedFiles} DurationMs={DurationMs}",
        question.Length,
        retrievedChunkCount,
        string.Join(',', retrievedFilenames),
        stopwatch.ElapsedMilliseconds);

    return Results.Ok(new { answer = response.Value.Content[0].Text });
});

if (!bypassAuthInDevelopment)
{
    askEndpoint.RequireAuthorization("ApiAccessPolicy");
}

app.Run();

public record AskRequest(string Question);
public partial class Program { }
