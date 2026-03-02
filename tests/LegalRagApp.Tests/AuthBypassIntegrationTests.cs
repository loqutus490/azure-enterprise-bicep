using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LegalRagApp.Tests;

public class AuthBypassIntegrationTests
{
    [Fact]
    public async Task Ask_ReturnsBadRequest_WithoutToken_WhenDevelopmentBypassEnabled()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/ask",
            new StringContent("""{"question":""}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ask_ReturnsUnauthorized_WithoutToken_WhenDevelopmentBypassDisabled()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/ask",
            new StringContent("""{"question":"hello"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ask_ReturnsUnauthorized_WithoutToken_WhenProductionEvenIfBypassFlagIsTrue()
    {
        await using var factory = new LegalRagAppFactory("Production", bypassAuthInDevelopment: true);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/ask",
            new StringContent("""{"question":"hello"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

internal sealed class LegalRagAppFactory(string environmentName, bool bypassAuthInDevelopment) : WebApplicationFactory<Program>
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
                ["AzureOpenAI:Key"] = "test-key",
                ["AzureOpenAI:Deployment"] = "chat-deployment",
                ["AzureOpenAI:EmbeddingDeployment"] = "embedding-deployment",
                ["AzureSearch:Endpoint"] = "https://example-search.search.windows.net",
                ["AzureSearch:Key"] = "test-key",
                ["AzureSearch:Index"] = "legal-index",
                ["Authorization:RequiredRole"] = "Api.Access",
                ["Authorization:BypassAuthInDevelopment"] = bypassAuthInDevelopment ? "true" : "false"
            };

            configBuilder.AddInMemoryCollection(testConfig);
        });
    }
}
