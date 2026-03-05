using Azure.Identity;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

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
var matterIdClaimTypes = builder.Configuration.GetSection("Authorization:MatterIdClaimTypes").Get<string[]>() ??
    new[] { "matter_ids", "matters", "matterId", "extension_matterIds" };
var bypassMatterAuthorizationInDevelopment = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("Authorization:BypassMatterAuthorizationInDevelopment");
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
            var scopeClaim = user.FindFirst("scp")?.Value
                ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/scope")?.Value;

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

if (bypassMatterAuthorizationInDevelopment)
{
    logger.LogWarning("Matter-level claim authorization bypass is enabled for Development. Do not enable this outside local debugging.");
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
var searchApiKey = builder.Configuration["AzureSearch:ApiKey"];
var maxContextCharacters = builder.Configuration.GetValue<int?>("Rag:MaxContextCharacters") ?? 12000;

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
SearchClient searchClient = string.IsNullOrWhiteSpace(searchApiKey)
    ? new SearchClient(searchEndpoint, searchIndexName, credential)
    : new SearchClient(searchEndpoint, searchIndexName, new AzureKeyCredential(searchApiKey));

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
var askEndpoint = app.MapPost("/ask", async (AskRequest request, HttpContext httpContext, CancellationToken cancellationToken) =>
{
    var stopwatch = Stopwatch.StartNew();

    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest("Question is required.");

    if (string.IsNullOrWhiteSpace(request.MatterId))
    {
        logger.LogWarning("AskRequest rejected due to missing matterId. QuestionLength={QuestionLength}", request.Question.Length);
        return Results.BadRequest("matterId is required.");
    }

    if (!bypassMatterAuthorizationInDevelopment)
    {
        var authorizedMatterIds = GetAuthorizedMatterIds(httpContext.User, matterIdClaimTypes);

        if (authorizedMatterIds.Count == 0)
        {
            logger.LogWarning("AskRequest denied due to missing matter claims. MatterId={MatterId}", request.MatterId);
            return Results.Forbid();
        }

        if (!authorizedMatterIds.Contains(request.MatterId))
        {
            logger.LogWarning("AskRequest denied by matter authorization. RequestedMatterId={RequestedMatterId}", request.MatterId);
            return Results.Forbid();
        }
    }

    var question = request.Question;
    var filters = new List<string>
    {
        $"matterId eq '{EscapeODataString(request.MatterId)}'"
    };

    if (!string.IsNullOrWhiteSpace(request.PracticeArea))
        filters.Add($"practiceArea eq '{EscapeODataString(request.PracticeArea)}'");

    if (!string.IsNullOrWhiteSpace(request.Client))
        filters.Add($"client eq '{EscapeODataString(request.Client)}'");

    if (!string.IsNullOrWhiteSpace(request.ConfidentialityLevel))
        filters.Add($"confidentialityLevel eq '{EscapeODataString(request.ConfidentialityLevel)}'");

    var searchFilter = string.Join(" and ", filters);

    var chatClient = openAiClient.GetChatClient(chatDeploymentName);
    var embeddingClient = openAiClient.GetEmbeddingClient(embeddingDeploymentName);

    var embeddingResponse = await embeddingClient.GenerateEmbeddingAsync(
        question,
        options: new OpenAI.Embeddings.EmbeddingGenerationOptions(),
        cancellationToken: cancellationToken);
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
        },
        Filter = searchFilter
    };
    searchOptions.Select.Add("content");
    searchOptions.Select.Add("source");
    searchOptions.Select.Add("matterId");
    searchOptions.Select.Add("practiceArea");
    searchOptions.Select.Add("client");
    searchOptions.Select.Add("confidentialityLevel");

    try
    {
        var searchResults =
            await searchClient.SearchAsync<SearchDocument>(question, searchOptions);

        var contextParts = new List<string>();
        var contextLength = 0;
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

            if (!result.Document.ContainsKey("content"))
                continue;

            var content = result.Document["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var contextChunk = string.IsNullOrWhiteSpace(sourceOrFilename)
                ? content
                : $"Source: {sourceOrFilename}\n{content}";

            if (contextLength + contextChunk.Length > maxContextCharacters)
                continue;

            contextParts.Add(contextChunk);
            contextLength += contextChunk.Length + 1;
        }

        var context = string.Join('\n', contextParts);
        var retrievedSources = retrievedFilenames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (string.IsNullOrWhiteSpace(context))
        {
            logger.LogInformation("AskRequest completed with no retrieval results. QuestionLength={QuestionLength} MatterId={MatterId} SearchFilter={SearchFilter} RetrievedChunkCount={RetrievedChunkCount} DurationMs={DurationMs}",
                question.Length,
                request.MatterId,
                searchFilter,
                retrievedChunkCount,
                stopwatch.ElapsedMilliseconds);

            return Results.Ok(new
            {
                answer = "No relevant documents found.",
                sources = Array.Empty<string>(),
                retrievedChunkCount
            });
        }

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new OpenAI.Chat.SystemChatMessage(
                """
You are a legal AI assistant for internal law firm use.
Rules:
- Answer using only the provided context.
- If context is insufficient, reply exactly: "Insufficient information in approved documents."
- Do not provide legal advice beyond the source context.
- Be concise, factual, and cite filenames when available.
"""
            ),
            new OpenAI.Chat.UserChatMessage($"Context:\n{context}\n\nQuestion:\n{question}")
        };

        var response = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        var answerText = response.Value.Content.FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(answerText))
        {
            logger.LogWarning("AskRequest completed with empty model response. QuestionLength={QuestionLength} RetrievedChunkCount={RetrievedChunkCount} DurationMs={DurationMs}",
                question.Length,
                retrievedChunkCount,
                stopwatch.ElapsedMilliseconds);
            return Results.Problem("Model returned an empty response.");
        }

        logger.LogInformation("AskRequest completed. QuestionLength={QuestionLength} MatterId={MatterId} SearchFilter={SearchFilter} RetrievedChunkCount={RetrievedChunkCount} RetrievedFiles={RetrievedFiles} DurationMs={DurationMs}",
            question.Length,
            request.MatterId,
            searchFilter,
            retrievedChunkCount,
            string.Join(',', retrievedSources),
            stopwatch.ElapsedMilliseconds);

        return Results.Ok(new
        {
            answer = answerText,
            sources = retrievedSources,
            retrievedChunkCount
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "AskRequest failed. QuestionLength={QuestionLength} DurationMs={DurationMs}", question.Length, stopwatch.ElapsedMilliseconds);
        if (app.Environment.IsDevelopment())
        {
            return Results.Problem($"Unable to process request at this time. {ex.GetType().Name}: {ex.Message}");
        }

        return Results.Problem("Unable to process request at this time.");
    }
});

if (!bypassAuthInDevelopment)
{
    askEndpoint.RequireAuthorization("ApiAccessPolicy");
}

app.Run();

static string EscapeODataString(string value) => value.Replace("'", "''");

static HashSet<string> GetAuthorizedMatterIds(System.Security.Claims.ClaimsPrincipal user, IEnumerable<string> claimTypes)
{
    var authorizedMatterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var claimType in claimTypes.Where(ct => !string.IsNullOrWhiteSpace(ct)))
    {
        foreach (var claim in user.FindAll(claimType))
        {
            foreach (var parsedMatterId in ParseMatterIds(claim.Value))
                authorizedMatterIds.Add(parsedMatterId);
        }
    }

    return authorizedMatterIds;
}

static IEnumerable<string> ParseMatterIds(string rawValue)
{
    if (string.IsNullOrWhiteSpace(rawValue))
        return Array.Empty<string>();

    var trimmed = rawValue.Trim();
    if (trimmed.StartsWith("[", StringComparison.Ordinal))
    {
        try
        {
            var jsonArray = JsonSerializer.Deserialize<string[]>(trimmed);
            if (jsonArray is { Length: > 0 })
                return jsonArray.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim());
        }
        catch
        {
            // Fall back to delimiter parsing.
        }
    }

    return trimmed
        .Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public record AskRequest(
    string Question,
    string MatterId,
    string? PracticeArea = null,
    string? Client = null,
    string? ConfidentialityLevel = null);
public partial class Program { }
