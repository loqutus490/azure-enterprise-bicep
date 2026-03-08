using System.Security.Claims;
using LegalRagApp.Models;
using LegalRagApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LegalRagApp.Tests;

public class AclFilterTests
{
    [Fact]
    public async Task GetUserClaims_ExtractsEmail_FromPreferredUsername()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IAuthorizationFilter>();

        var principal = MakePrincipal([
            new Claim("preferred_username", "lawyer@firm.com"),
            new Claim("permittedMatters", "MATTER-001")
        ]);

        var ctx = svc.GetUserClaims(principal);
        Assert.Equal("lawyer@firm.com", ctx.Email);
    }

    [Fact]
    public async Task BuildAclFilter_CombinesIdentityAndGroups()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IAuthorizationFilter>();

        var ctx = new UserClaimsContext
        {
            Email = "o'brien@firm.com",
            Groups = ["firm's-lawyers"],
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" }
        };

        var filter = svc.BuildAclFilter(ctx);

        Assert.Contains("allowedUsers", filter);
        Assert.Contains("allowedGroups", filter);
        Assert.Contains("o''brien@firm.com", filter);
        Assert.Contains("firm''s-lawyers", filter);
    }

    [Fact]
    public async Task BuildSecurityFilter_UsesPermittedMatters()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IAuthorizationFilter>();

        var ctx = new UserClaimsContext
        {
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001", "MATTER-002" }
        };

        var filter = svc.BuildSecurityFilter(ctx);
        Assert.Contains("MATTER-001", filter);
        Assert.Contains("MATTER-002", filter);
    }

    private static ClaimsPrincipal MakePrincipal(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        return new ClaimsPrincipal(identity);
    }
}
