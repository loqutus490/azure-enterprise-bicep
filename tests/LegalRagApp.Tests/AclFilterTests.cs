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

    [Fact]
    public async Task FilterAuthorizedChunks_KeepsOnlyChunksForPermittedMatter()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IAuthorizationFilter>();

        var userClaims = new UserClaimsContext
        {
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" },
            Groups = []
        };

        var chunks = new List<RetrievedChunk>
        {
            new() { MatterId = "MATTER-001", SourceFile = "authorized.txt" },
            new() { MatterId = "MATTER-002", SourceFile = "unauthorized.txt" }
        };

        var filtered = svc.FilterAuthorizedChunks(chunks, userClaims);

        Assert.Single(filtered);
        Assert.Equal("authorized.txt", filtered[0].SourceFile);
    }

    [Fact]
    public async Task FilterAuthorizedChunks_FiltersGroupRestrictedChunks_WhenGroupMissing()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IAuthorizationFilter>();

        var userClaims = new UserClaimsContext
        {
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" },
            Groups = ["litigation"]
        };

        var chunks = new List<RetrievedChunk>
        {
            new() { MatterId = "MATTER-001", AccessGroup = "litigation", SourceFile = "group-match.txt" },
            new() { MatterId = "MATTER-001", AccessGroup = "tax", SourceFile = "group-miss.txt" },
            new() { MatterId = "MATTER-001", SourceFile = "public-within-matter.txt" }
        };

        var filtered = svc.FilterAuthorizedChunks(chunks, userClaims);

        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, c => c.SourceFile == "group-match.txt");
        Assert.Contains(filtered, c => c.SourceFile == "public-within-matter.txt");
        Assert.DoesNotContain(filtered, c => c.SourceFile == "group-miss.txt");
    }

    [Fact]
    public async Task FilterAuthorizedChunks_UsesCaseInsensitiveGroupMatching()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IAuthorizationFilter>();

        var userClaims = new UserClaimsContext
        {
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" },
            Groups = ["LITIGATION-TEAM"]
        };

        var chunks = new List<RetrievedChunk>
        {
            new() { MatterId = "MATTER-001", AccessGroup = "litigation-team", SourceFile = "case-insensitive.txt" }
        };

        var filtered = svc.FilterAuthorizedChunks(chunks, userClaims);

        Assert.Single(filtered);
        Assert.Equal("case-insensitive.txt", filtered[0].SourceFile);
    }

    private static ClaimsPrincipal MakePrincipal(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        return new ClaimsPrincipal(identity);
    }
}
