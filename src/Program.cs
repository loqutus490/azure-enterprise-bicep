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

// 🔐 Enable auth middleware
app.UseAuthentication();
app.UseAuthorization();

// Managed Identity credential
var credential = new DefaultAzureCredential();

var openAiEndpointValue = builder.Configuration["AzureOpenAI:Endpoint"];
var chatDeploymentName = builder.Configuration["AzureOpenAI:Deployment"];
var searchEndpointValue = builder.Configuration["AzureSearch:Endpoint"];
var searchIndexName = builder.Configuration["AzureSearch:Index"];

if (string.IsNullOrWhiteSpace(openAiEndpointValue))
    throw new InvalidOperationException("Missing configuration: AzureOpenAI:Endpoint");

if (string.IsNullOrWhiteSpace(chatDeploymentName))
    throw new InvalidOperationException("Missing configuration: AzureOpenAI:Deployment");

if (string.IsNullOrWhiteSpace(searchEndpointValue))
    throw new InvalidOperationException("Missing configuration: AzureSearch:Endpoint");

if (string.IsNullOrWhiteSpace(searchIndexName))
    throw new InvalidOperationException("Missing configuration: AzureSearch:Index");

// Azure OpenAI
var openAiEndpoint = new Uri(openAiEndpointValue);
var openAiClient = new AzureOpenAIClient(openAiEndpoint, credential);

// Azure Search
var searchEndpoint = new Uri(searchEndpointValue);

var searchClient = new SearchClient(
    searchEndpoint,
    searchIndexName,
    credential
);

// Public health endpoint
app.MapGet("/ping", () => "pong");

// 🔐 Protected version endpoint
app.MapGet("/version", () => "SECURED_BUILD_V1")
    .RequireAuthorization("ApiAccessPolicy");

// 🔐 Protected RAG endpoint
app.MapPost("/ask", async (AskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest("Question is required.");

    var question = request.Question;

    var chatClient = openAiClient.GetChatClient(chatDeploymentName);

    var searchResults =
        await searchClient.SearchAsync<SearchDocument>(question);

    var context = "";

    await foreach (var result in searchResults.Value.GetResultsAsync())
    {
        if (result.Document.ContainsKey("content"))
            context += result.Document["content"]?.ToString() + "\n";
    }

    if (string.IsNullOrWhiteSpace(context))
        return Results.Ok(new { answer = "No relevant documents found." });

    var response = await chatClient.CompleteChatAsync(context + "\n\nQuestion: " + question);

    return Results.Ok(new { answer = response.Value.Content[0].Text });
})
.RequireAuthorization("ApiAccessPolicy");

app.Run();

public record AskRequest(string Question);