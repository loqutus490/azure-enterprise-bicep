using System.Security.Claims;
using LegalRagApp.Models;
using LegalRagApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LegalRagApp.Tests;

// ---------------------------------------------------------------------------
// AclFilterTests
//
// These tests verify the document-level ACL security trimming logic introduced
// in RetrievalService without requiring a live Azure AI Search index.
//
// Strategy
// --------
// BuildAclFilter and GetUserClaims are pure functions exposed on IRetrievalService.
// We exercise them through the real singleton registered by WebApplicationFactory
// so that any configuration-driven behaviour (GroupClaimTypes, EnableAclFilter) is
// also covered.  No network calls are made; the search endpoint in test config is a
// placeholder that is never dialled.
// ---------------------------------------------------------------------------
public class AclFilterTests
{
    // ------------------------------------------------------------------
    // GetUserClaims — email extraction
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetUserClaims_ExtractsEmail_FromPreferredUsername()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var claims = new[]
        {
            new Claim("preferred_username", "lawyer@firm.com"),
            new Claim("permittedMatters", "MATTER-001")
        };
        var principal = MakePrincipal(claims);

        var ctx = svc.GetUserClaims(principal);

        Assert.Equal("lawyer@firm.com", ctx.Email);
    }

    [Fact]
    public async Task GetUserClaims_ExtractsEmail_FallsBackToEmailClaim()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var claims = new[]
        {
            new Claim("email", "associate@firm.com"),
            new Claim("permittedMatters", "MATTER-001")
        };
        var principal = MakePrincipal(claims);

        var ctx = svc.GetUserClaims(principal);

        Assert.Equal("associate@firm.com", ctx.Email);
    }

    [Fact]
    public async Task GetUserClaims_ExtractsGroups_FromGroupsClaim()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var claims = new[]
        {
            new Claim("preferred_username", "lawyer@firm.com"),
            new Claim("groups", "litigation-team-guid"),
            new Claim("groups", "all-lawyers-guid"),
            new Claim("permittedMatters", "MATTER-001")
        };
        var principal = MakePrincipal(claims);

        var ctx = svc.GetUserClaims(principal);

        Assert.Contains("litigation-team-guid", ctx.Groups);
        Assert.Contains("all-lawyers-guid", ctx.Groups);
        Assert.Equal(2, ctx.Groups.Count);
    }

    [Fact]
    public async Task GetUserClaims_ReturnsEmptyEmail_WhenNoIdentityClaims()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var principal = MakePrincipal(Array.Empty<Claim>());

        var ctx = svc.GetUserClaims(principal);

        Assert.Equal(string.Empty, ctx.Email);
        Assert.Empty(ctx.Groups);
    }

    // ------------------------------------------------------------------
    // BuildAclFilter — filter expression construction
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildAclFilter_IncludesUserEmail_InAllowedUsersClause()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var ctx = new UserClaimsContext
        {
            Email = "userA@firm.com",
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" }
        };

        var filter = svc.BuildAclFilter(ctx);

        Assert.Contains("allowedUsers/any(u: u eq 'userA@firm.com')", filter);
    }

    [Fact]
    public async Task BuildAclFilter_IncludesAllGroups_InAllowedGroupsClauses()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var ctx = new UserClaimsContext
        {
            Email = "lawyer@firm.com",
            Groups = new[] { "litigation-team", "all-lawyers" },
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" }
        };

        var filter = svc.BuildAclFilter(ctx);

        Assert.Contains("allowedGroups/any(g: g eq 'litigation-team')", filter);
        Assert.Contains("allowedGroups/any(g: g eq 'all-lawyers')", filter);
    }

    [Fact]
    public async Task BuildAclFilter_CombinesClausesWithOr()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var ctx = new UserClaimsContext
        {
            Email = "lawyer@firm.com",
            Groups = new[] { "litigation-team" },
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" }
        };

        var filter = svc.BuildAclFilter(ctx);

        // Both user and group clauses must be joined by OR inside a single group.
        Assert.StartsWith("(", filter);
        Assert.EndsWith(")", filter);
        Assert.Contains(" or ", filter);
    }

    [Fact]
    public async Task BuildAclFilter_ReturnsEmpty_WhenContextHasNoEmailOrGroups()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var ctx = new UserClaimsContext
        {
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" }
        };

        var filter = svc.BuildAclFilter(ctx);

        Assert.Equal(string.Empty, filter);
    }

    [Fact]
    public async Task BuildAclFilter_EscapesSingleQuotes_InEmailAndGroups()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var ctx = new UserClaimsContext
        {
            Email = "o'brien@firm.com",
            Groups = new[] { "firm's-lawyers" },
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" }
        };

        var filter = svc.BuildAclFilter(ctx);

        // Single quotes must be doubled for OData safety.
        Assert.Contains("o''brien@firm.com", filter);
        Assert.Contains("firm''s-lawyers", filter);
    }

    // ------------------------------------------------------------------
    // AskEndpoint_RespectsDocumentACL
    //
    // This test verifies that two users with different identities produce
    // non-overlapping ACL filters — ensuring that the search query sent for
    // userA can never return documents exclusively granted to userB, and
    // vice versa.  Full end-to-end enforcement requires a real index with
    // ACL fields populated; the filter-construction test here provides the
    // deterministic gate that is always safe to run in CI.
    // ------------------------------------------------------------------

    [Fact]
    public async Task AskEndpoint_RespectsDocumentACL_FiltersAreUserSpecific()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        // userA has access to contract1.txt (litigation-team).
        var userAContext = new UserClaimsContext
        {
            Email = "userA@firm.com",
            Groups = new[] { "litigation-team" },
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" }
        };

        // userB has access to sample-nda-matter-001.txt (corporate-team).
        var userBContext = new UserClaimsContext
        {
            Email = "userB@firm.com",
            Groups = new[] { "corporate-team" },
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" }
        };

        var filterA = svc.BuildAclFilter(userAContext);
        var filterB = svc.BuildAclFilter(userBContext);

        // userA's filter must NOT reference userB's identity.
        Assert.DoesNotContain("userB@firm.com", filterA);
        Assert.DoesNotContain("corporate-team", filterA);

        // userB's filter must NOT reference userA's identity.
        Assert.DoesNotContain("userA@firm.com", filterB);
        Assert.DoesNotContain("litigation-team", filterB);

        // Each filter must reference only the respective user's own identity.
        Assert.Contains("userA@firm.com", filterA);
        Assert.Contains("litigation-team", filterA);
        Assert.Contains("userB@firm.com", filterB);
        Assert.Contains("corporate-team", filterB);
    }

    [Fact]
    public async Task AskEndpoint_RespectsDocumentACL_MatterFiltersAreUserSpecific()
    {
        await using var factory = new LegalRagAppFactory("Development", bypassAuthInDevelopment: true);
        var svc = factory.Services.GetRequiredService<IRetrievalService>();

        var userAContext = new UserClaimsContext
        {
            Email = "userA@firm.com",
            Groups = Array.Empty<string>(),
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" }
        };

        var userBContext = new UserClaimsContext
        {
            Email = "userB@firm.com",
            Groups = Array.Empty<string>(),
            PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-002" }
        };

        var matterFilterA = svc.BuildSecurityFilter(userAContext);
        var matterFilterB = svc.BuildSecurityFilter(userBContext);

        // Matter filters must be completely disjoint.
        Assert.Contains("MATTER-001", matterFilterA);
        Assert.DoesNotContain("MATTER-002", matterFilterA);
        Assert.Contains("MATTER-002", matterFilterB);
        Assert.DoesNotContain("MATTER-001", matterFilterB);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ClaimsPrincipal MakePrincipal(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        return new ClaimsPrincipal(identity);
    }
}
