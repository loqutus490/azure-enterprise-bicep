using System.Net;
using System.Text;

namespace LegalRagApp.Tests;

public class AuthBypassIntegrationTests
{
    [Fact]
    public async Task Ask_ReturnsBadRequest_WithoutToken_WhenDevelopmentBypassEnabled()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/ask", new StringContent("""{"question":""}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ask_ReturnsUnauthorized_WithoutToken_WhenDevelopmentBypassDisabled()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/ask", new StringContent("""{"question":"hello"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
