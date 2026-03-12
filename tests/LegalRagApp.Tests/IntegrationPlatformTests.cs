using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using LegalRagApp.Models;
using LegalRagApp.Prompts;
using LegalRagApp.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LegalRagApp.Tests;

public class IntegrationPlatformTests
{
    [Fact]
    public async Task AuthorizedUserReceivesGroundedAnswerWithSourceMetadata()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "authorized");

        var response = await client.PostAsJsonAsync("/ask", new AskRequestDto { Question = "What is the termination clause?", MatterId = "MATTER-001", ConversationId = "c1" });
        var payload = await ReadJsonResponseAsync<AskResponseDto>(response);

        Assert.NotNull(payload);
        Assert.Equal("grounded_success", payload!.Diagnostics!.FinalAnswerStatus);
        Assert.NotEmpty(payload.SourceMetadata);
        Assert.NotEqual(PromptOutputFactory.InsufficientContextSummary, payload.Answer);
        Assert.True(payload.RetrievedChunkCount > 0);
    }

    [Fact]
    public async Task UnauthorizedUserGetsFallbackWhenDocumentsFilteredOut()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "unauthorized");

        var response = await client.PostAsJsonAsync("/ask", new AskRequestDto { Question = "What is the termination clause?", MatterId = "MATTER-001", ConversationId = "c2" });
        var payload = await ReadJsonResponseAsync<AskResponseDto>(response);

        Assert.Equal(PromptOutputFactory.InsufficientContextSummary, payload!.Answer);
        Assert.Equal(0, payload.RetrievedChunkCount);
        Assert.Equal("fallback_unauthorized", payload.Diagnostics!.FallbackReason);
    }

    [Fact]
    public async Task NoDocumentsRetrievedReturnsFallback()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "authorized");

        var response = await client.PostAsJsonAsync("/ask", new AskRequestDto { Question = "no-docs scenario", MatterId = "MATTER-001", ConversationId = "c3" });
        var payload = await ReadJsonResponseAsync<AskResponseDto>(response);

        Assert.Equal(PromptOutputFactory.InsufficientContextSummary, payload!.Answer);
        Assert.Equal("fallback_no_docs", payload.Diagnostics!.FallbackReason);
    }


    [Fact]
    public async Task SuccessfulDocumentRetrieval_ReturnsAuthorizedChunkMetadata()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "authorized");

        var response = await client.PostAsJsonAsync("/ask", new AskRequestDto
        {
            Question = "summarize the agreement",
            MatterId = "MATTER-001",
            ConversationId = "scenario-success"
        });

        var payload = await response.Content.ReadFromJsonAsync<AskResponseDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.True(payload!.RetrievedChunkCount > 0);
        Assert.NotEmpty(payload.SourceMetadata);
        Assert.All(payload.SourceMetadata, source => Assert.False(string.IsNullOrWhiteSpace(source.DocumentType)));
    }

    [Fact]
    public async Task AuthorizationFiltering_UnauthorizedUserGetsNoDocuments()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "unauthorized");

        var askResponse = await client.PostAsJsonAsync("/ask", new AskRequestDto
        {
            Question = "summarize the agreement",
            MatterId = "MATTER-001",
            ConversationId = "scenario-unauthorized"
        });
        var askPayload = await askResponse.Content.ReadFromJsonAsync<AskResponseDto>();

        var debugResponse = await client.PostAsJsonAsync("/debug/retrieval", new AskRequestDto
        {
            Question = "summarize the agreement",
            MatterId = "MATTER-001",
            ConversationId = "scenario-unauthorized-debug"
        });
        var debugPayload = await debugResponse.Content.ReadFromJsonAsync<RetrievalDebugResponseDto>();

        Assert.Equal(HttpStatusCode.OK, askResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, debugResponse.StatusCode);
        Assert.NotNull(askPayload);
        Assert.NotNull(debugPayload);
        Assert.Equal(0, askPayload!.RetrievedChunkCount);
        Assert.Equal(2, debugPayload!.RawRetrievalCount);
        Assert.Equal(0, debugPayload.FilteredRetrievalCount);
    }

    [Fact]
    public async Task EmptyRetrievalResult_ReturnsZeroCounts()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "authorized");

        var debugResponse = await client.PostAsJsonAsync("/debug/retrieval", new AskRequestDto
        {
            Question = "no-docs scenario",
            MatterId = "MATTER-001",
            ConversationId = "scenario-empty-debug"
        });

        var debugPayload = await debugResponse.Content.ReadFromJsonAsync<RetrievalDebugResponseDto>();

        Assert.Equal(HttpStatusCode.OK, debugResponse.StatusCode);
        Assert.NotNull(debugPayload);
        Assert.Equal(0, debugPayload!.RawRetrievalCount);
        Assert.Equal(0, debugPayload.FilteredRetrievalCount);
        Assert.Equal("fallback_no_docs", debugPayload.FallbackReason);
    }

    [Fact]
    public async Task FallbackResponseWhenNoContextExists_ReturnsInsufficientInformation()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "authorized");

        var response = await client.PostAsJsonAsync("/ask", new AskRequestDto
        {
            Question = "no-docs scenario",
            MatterId = "MATTER-001",
            ConversationId = "scenario-fallback"
        });

        var payload = await response.Content.ReadFromJsonAsync<AskResponseDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal(PromptOutputFactory.InsufficientContextSummary, payload!.Answer);
        Assert.Equal(0, payload.RetrievedChunkCount);
        Assert.Equal("fallback_no_docs", payload.Diagnostics!.FallbackReason);
    }

    [Fact]
    public async Task DebugEndpointBlockedWhenDisabled()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "authorized");

        var response = await client.PostAsJsonAsync("/debug/retrieval", new AskRequestDto { Question = "q", MatterId = "MATTER-001", ConversationId = "d1" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DebugEndpointWorksWhenEnabled()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "authorized");

        var response = await client.PostAsJsonAsync("/debug/retrieval", new AskRequestDto { Question = "q", MatterId = "MATTER-001", ConversationId = "d2" });
        var payload = await ReadJsonResponseAsync<RetrievalDebugResponseDto>(response);
        Assert.NotNull(payload);
        Assert.True(payload!.FilteredRetrievalCount > 0);
    }

    [Fact]
    public async Task DebugEndpointRequiresAuthenticationWhenBypassDisabled()
    {
        await using var factory = new LegalRagAppFactory("Development", false, debugRagEnabled: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/debug/retrieval", new AskRequestDto { Question = "q", MatterId = "MATTER-001", ConversationId = "d3" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AskDiagnosticsSummaryHiddenWhenDebugDisabled()
    {
        await using var factory = new LegalRagAppFactory("Development", true, debugRagEnabled: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "authorized");

        var response = await client.PostAsJsonAsync("/ask", new AskRequestDto { Question = "What is the termination clause?", MatterId = "MATTER-001", ConversationId = "diag-disabled" });
        var payload = await ReadJsonResponseAsync<AskResponseDto>(response);

        Assert.NotNull(payload);
        Assert.Null(payload!.Diagnostics);
    }

    private static async Task<T> ReadJsonResponseAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            contentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true,
            $"Expected JSON but got '{contentType ?? "<null>"}'. Body:
{body}");

        var payload = JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(payload);
        return payload!;
    }
}

public sealed class LegalRagAppFactory(string environmentName, bool bypassAuthInDevelopment, bool debugRagEnabled = false) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environmentName);
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var testConfig = new Dictionary<string, string?>
            {
                ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
                ["AzureAd:ClientId"] = "11111111-1111-1111-1111-111111111111",
                ["AzureAd:Audience"] = "11111111-1111-1111-1111-111111111111",
                ["AzureOpenAI:Endpoint"] = "https://example-openai.openai.azure.com/",
                ["AzureOpenAI:Deployment"] = "chat-deployment",
                ["AzureOpenAI:EmbeddingDeployment"] = "embedding-deployment",
                ["AzureSearch:Endpoint"] = "https://example-search.search.windows.net",
                ["AzureSearch:Index"] = "legal-index",
                ["Authorization:RequiredRole"] = "Api.Access",
                ["Authorization:EnableAzureAd"] = "true",
                ["Authorization:BypassAuthInDevelopment"] = bypassAuthInDevelopment ? "true" : "false",
                ["Authorization:BypassMatterAuthorizationInDevelopment"] = "true",
                ["DebugRag:Enabled"] = debugRagEnabled ? "true" : "false"
            };

            configBuilder.AddInMemoryCollection(testConfig);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                    options.DefaultScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            services.AddSingleton<IRetrievalService, FakeRetrievalService>();
            services.AddSingleton<IChatService, FakeChatService>();
            services.AddSingleton<IQueryRewriteService, FakeQueryRewriteService>();
        });
    }
}

internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var value))
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Test-User header."));

        var userType = value.ToString();
        var claims = new List<Claim>
        {
            new("permittedMatters", "MATTER-001"),
            new("preferred_username", userType == "unauthorized" ? "unauthorized@firm.com" : "authorized@firm.com"),
            new("scp", "access_as_user")
        };

        if (userType == "authorized")
            claims.Add(new Claim("groups", "team-a"));

        var identity = new ClaimsIdentity(claims, "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class FakeRetrievalService(IAuthorizationFilter authorizationFilter) : IRetrievalService
{
    public Task<RetrievalResult> RetrieveAsync(AskRequestDto request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userClaims = authorizationFilter.GetUserClaims(user);

        if (request.Question.Contains("no-docs", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new RetrievalResult
            {
                Chunks = Array.Empty<RetrievedChunk>(),
                RawRetrievedChunkCount = 0,
                FilteredRetrievedChunkCount = 0,
                UserClaims = userClaims,
                FallbackReason = "fallback_no_docs"
            });
        }

        var rawChunks = new List<RetrievedChunk>
        {
            new()
            {
                DocumentId = "doc-1",
                SourceId = "src-1",
                SourceFile = "master-services-agreement.pdf",
                MatterId = request.MatterId,
                AccessGroup = "team-a",
                DocumentType = "contract",
                Content = "Termination requires 30 days written notice.",
                Snippet = "Termination requires 30 days written notice."
            },
            new()
            {
                DocumentId = "doc-2",
                SourceId = "src-2",
                SourceFile = "missing-metadata.pdf",
                MatterId = request.MatterId,
                AccessGroup = "team-a",
                Content = "This chunk is missing documentType and should be filtered.",
                Snippet = "This chunk is missing documentType and should be filtered."
            }
        };

        var filteredChunks = authorizationFilter.FilterAuthorizedChunks(rawChunks, userClaims);
        return Task.FromResult(new RetrievalResult
        {
            Chunks = filteredChunks,
            RawRetrievedChunkCount = rawChunks.Count,
            FilteredRetrievedChunkCount = filteredChunks.Count,
            AverageScore = filteredChunks.Count > 0 ? 0.9 : 0.0,
            UserClaims = userClaims,
            FallbackReason = filteredChunks.Count == 0 ? "fallback_unauthorized" : null
        });
    }

    public async Task<RetrievalDebugResponseDto> BuildDebugAsync(AskRequestDto request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var retrieval = await RetrieveAsync(request, user, cancellationToken);
        return new RetrievalDebugResponseDto
        {
            Query = request.Question,
            User = retrieval.UserClaims,
            RawRetrievalCount = retrieval.RawRetrievedChunkCount,
            FilteredRetrievalCount = retrieval.FilteredRetrievedChunkCount,
            PromptContextPreview = retrieval.Chunks.FirstOrDefault()?.Snippet ?? string.Empty,
            FallbackReason = retrieval.FallbackReason,
            Sources = retrieval.Chunks.Select(c => new AskSourceDto
            {
                SourceFile = c.SourceFile ?? string.Empty,
                SourceId = c.SourceId ?? string.Empty,
                MatterId = c.MatterId ?? string.Empty,
                DocumentType = c.DocumentType ?? string.Empty,
                AccessGroup = c.AccessGroup ?? string.Empty
            }).ToList()
        };
    }
}

internal sealed class FakeChatService : IChatService
{
    public string ModelUsed => "fake-chat";

    public Task<ChatGenerationResult> GenerateStructuredAnswerAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ChatGenerationResult
        {
            Answer = new StructuredAnswerDto
            {
                Summary = "Termination requires 30 days written notice.",
                KeyPoints = ["30-day written notice is required."],
                Citations = [new CitationDto { DocumentId = "doc-1", Document = "master-services-agreement.pdf", Excerpt = "Termination requires 30 days written notice." }],
                Confidence = "high"
            }
        });
    }
}

internal sealed class FakeQueryRewriteService : IQueryRewriteService
{
    public Task<string> RewriteAsync(string question, CancellationToken cancellationToken)
        => Task.FromResult(question);
}
