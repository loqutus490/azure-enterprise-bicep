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
var allowAnyClientApp = builder.Configuration.GetValue<bool>("Authorization:AllowAnyClientApp");

if (!builder.Environment.IsDevelopment() && allowedClientAppIdSet.Count == 0 && !allowAnyClientApp)
{
    throw new InvalidOperationException(
        "Authorization:AllowedClientAppIds must include at least one client app ID outside Development. " +
        "Set Authorization:AllowAnyClientApp=true only for controlled migration scenarios.");
}

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
var maxContextCharacters = builder.Configuration.GetValue<int?>("Rag:MaxContextCharacters") ?? 12000;

if (maxContextCharacters < 2000)
    throw new InvalidOperationException("Rag:MaxContextCharacters must be at least 2000.");

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
var askEndpoint = app.MapPost("/ask", async (AskRequest request, CancellationToken cancellationToken) =>
{
    var stopwatch = Stopwatch.StartNew();

    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest("Question is required.");

    var question = request.Question;

    var chatClient = openAiClient.GetChatClient(chatDeploymentName);
    var embeddingClient = openAiClient.GetEmbeddingClient(embeddingDeploymentName);

    var embeddingResponse = await embeddingClient.GenerateEmbeddingAsync(question, cancellationToken);
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
    searchOptions.Select.Add("filename");

    var searchResults =
        await searchClient.SearchAsync<SearchDocument>(question, searchOptions, cancellationToken);

    var contextParts = new List<string>();
    var contextLength = 0;
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

        if (!result.Document.ContainsKey("content"))
            continue;

        var content = result.Document["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(content))
            continue;

        var filenameForContext = result.Document.ContainsKey("filename")
            ? result.Document["filename"]?.ToString()
            : null;
        var contextChunk = string.IsNullOrWhiteSpace(filenameForContext)
            ? content
            : $"Source: {filenameForContext}\n{content}";

        if (contextLength + contextChunk.Length > maxContextCharacters)
            break;

        contextParts.Add(contextChunk);
        contextLength += contextChunk.Length + 1;
    }

    var context = string.Join('\n', contextParts);

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

    var response = await chatClient.CompleteChatAsync(controlledPrompt, cancellationToken: cancellationToken);

    var sources = retrievedFilenames.OrderBy(filename => filename, StringComparer.OrdinalIgnoreCase).ToArray();

    logger.LogInformation("AskRequest completed. QuestionLength={QuestionLength} RetrievedChunkCount={RetrievedChunkCount} RetrievedFiles={RetrievedFiles} DurationMs={DurationMs}",
        question.Length,
        retrievedChunkCount,
        string.Join(',', retrievedFilenames),
        stopwatch.ElapsedMilliseconds);

    return Results.Ok(new
    {
        answer = response.Value.Content[0].Text,
        sources
    });
});

if (!bypassAuthInDevelopment)
{
    askEndpoint.RequireAuthorization("ApiAccessPolicy");
}

app.Run();

public record AskRequest(string Question);
public partial class Program { }
