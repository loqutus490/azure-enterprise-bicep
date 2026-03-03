using Azure.Identity;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using System.Diagnostics;
using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// 🔐 Add Entra authentication
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

var requiredApiRole = builder.Configuration["Authorization:RequiredRole"] ?? "Api.Access";
var requiredScope = builder.Configuration["Authorization:RequiredScope"] ?? "access_as_user";
var allowedClientAppIds = builder.Configuration.GetSection("Authorization:AllowedClientAppIds").Get<string[]>() ?? Array.Empty<string>();
var allowedClientAppIdSet = new HashSet<string>(
    allowedClientAppIds.Where(clientAppId => !string.IsNullOrWhiteSpace(clientAppId)),
    StringComparer.OrdinalIgnoreCase);
var corsAllowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        if (corsAllowedOrigins.Length > 0)
        {
            policy.WithOrigins(corsAllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiAccessPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var user = context.User;
            var appId = user.FindFirst("azp")?.Value ?? user.FindFirst("appid")?.Value;
            var scopeClaim = user.FindFirst("scp")?.Value;

            if (!string.IsNullOrWhiteSpace(scopeClaim))
            {
                var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var hasRequiredScope = scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
                if (!hasRequiredScope)
                    return false;

                if (allowedClientAppIdSet.Count == 0)
                    return true;

                return !string.IsNullOrWhiteSpace(appId) && allowedClientAppIdSet.Contains(appId);
            }

            if (string.IsNullOrWhiteSpace(appId))
                return false;

            var idType = user.FindFirst("idtyp")?.Value;
            if (!string.IsNullOrWhiteSpace(idType) && !string.Equals(idType, "app", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!user.IsInRole(requiredApiRole))
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

if (allowedClientAppIdSet.Count == 0 && !builder.Environment.IsDevelopment())
{
    logger.LogWarning("Authorization:AllowedClientAppIds is empty outside Development. Any caller app with required scope/role may be allowed.");
}

if (bypassAuthInDevelopment)
{
    logger.LogWarning("Authorization bypass is enabled for Development. Do not enable this outside local debugging.");
}

// Serve the built-in web chat UI from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowFrontend");

// 🔐 Enable auth middleware
app.UseAuthentication();
app.UseAuthorization();

// Managed Identity credential
var credential = new DefaultAzureCredential();

var openAiEndpointValue = builder.Configuration["AzureOpenAI:Endpoint"];
var chatDeploymentName = builder.Configuration["AzureOpenAI:Deployment"];
var embeddingDeploymentName = builder.Configuration["AzureOpenAI:EmbeddingDeployment"];
var searchEndpointValue = builder.Configuration["AzureSearch:Endpoint"];
var searchIndexName = builder.Configuration["AzureSearch:Index"];

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
AzureOpenAIClient openAiClient = new AzureOpenAIClient(openAiEndpoint, credential);

// Azure Search
var searchEndpoint = new Uri(searchEndpointValue);
SearchClient searchClient = new SearchClient(searchEndpoint, searchIndexName, credential);

// Public health endpoint
app.MapGet("/ping", () => "pong");
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

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
    searchOptions.Select.Add("source");
    searchOptions.Select.Add("filename");

    try
    {
        var searchResults =
            await searchClient.SearchAsync<SearchDocument>(question, searchOptions);

        var contextBuilder = new StringBuilder();
        var retrievedFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retrievedChunkCount = 0;

        await foreach (var result in searchResults.Value.GetResultsAsync())
        {
            retrievedChunkCount++;

            // Prefer 'source' (used by ingestion), then fallback to 'filename'.
            var sourceOrFilename = result.Document.TryGetValue("source", out var sourceValue)
                ? sourceValue?.ToString()
                : result.Document.TryGetValue("filename", out var filenameValue)
                    ? filenameValue?.ToString()
                    : null;

            if (!string.IsNullOrWhiteSpace(sourceOrFilename))
                retrievedFilenames.Add(sourceOrFilename);

            if (result.Document.ContainsKey("content"))
                contextBuilder.AppendLine(result.Document["content"]?.ToString());
        }

        var context = contextBuilder.ToString();
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
        var answerText = response.Value.Content.FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(answerText))
        {
            logger.LogWarning("AskRequest completed with empty model response. QuestionLength={QuestionLength} RetrievedChunkCount={RetrievedChunkCount} DurationMs={DurationMs}",
                question.Length,
                retrievedChunkCount,
                stopwatch.ElapsedMilliseconds);
            return Results.Problem("Model returned an empty response.");
        }

        logger.LogInformation("AskRequest completed. QuestionLength={QuestionLength} RetrievedChunkCount={RetrievedChunkCount} RetrievedFiles={RetrievedFiles} DurationMs={DurationMs}",
            question.Length,
            retrievedChunkCount,
            string.Join(',', retrievedFilenames),
            stopwatch.ElapsedMilliseconds);

        return Results.Ok(new { answer = answerText });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "AskRequest failed. QuestionLength={QuestionLength} DurationMs={DurationMs}", question.Length, stopwatch.ElapsedMilliseconds);
        return Results.Problem("Unable to process request at this time.");
    }
});

if (!bypassAuthInDevelopment)
{
    askEndpoint.RequireAuthorization("ApiAccessPolicy");
}

app.Run();

public record AskRequest(string Question);
public partial class Program { }
