using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

public sealed class AuthorizationFilter : IAuthorizationFilter
{
    private readonly string[] _permittedMatterClaimTypes;
    private readonly string[] _groupClaimTypes;

    public AuthorizationFilter(IConfiguration configuration)
    {
        _permittedMatterClaimTypes = configuration.GetSection("Authorization:PermittedMattersClaimTypes").Get<string[]>()
            ?? new[] { "permittedMatters", "matter_ids", "matters", "matterId", "extension_matterIds" };
        _groupClaimTypes = configuration.GetSection("Authorization:GroupClaimTypes").Get<string[]>()
            ?? new[] { "groups", "group", "roles", "accessGroup" };
    }

    public UserClaimsContext GetUserClaims(ClaimsPrincipal user)
    {
        var userId = user.FindFirst("userId")?.Value
            ?? user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst(ClaimTypes.Upn)?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "anonymous";

        var email = user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst("upn")?.Value
            ?? user.FindFirst("unique_name")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst(ClaimTypes.Upn)?.Value
            ?? string.Empty;

        var role = user.FindFirst("role")?.Value
            ?? user.FindFirst(ClaimTypes.Role)?.Value
            ?? "unknown";

        var permittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claimType in _permittedMatterClaimTypes.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            foreach (var claim in user.FindAll(claimType))
            {
                foreach (var matter in ParseMatterIds(claim.Value))
                    permittedMatters.Add(matter);
            }
        }

        var groups = _groupClaimTypes
            .SelectMany(claimType => user.FindAll(claimType))
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UserClaimsContext
        {
            UserId = userId,
            Email = email,
            Role = role,
            PermittedMatters = permittedMatters,
            Groups = groups
        };
    }

    public bool IsMatterAuthorized(UserClaimsContext userClaims, string matterId) =>
        userClaims.PermittedMatters.Contains(matterId);

    public IReadOnlyList<RetrievedChunk> FilterAuthorizedChunks(IReadOnlyList<RetrievedChunk> chunks, UserClaimsContext userClaims)
    {
        return chunks.Where(chunk =>
                (!string.IsNullOrWhiteSpace(chunk.MatterId) && userClaims.PermittedMatters.Contains(chunk.MatterId))
                && (string.IsNullOrWhiteSpace(chunk.AccessGroup) || userClaims.Groups.Contains(chunk.AccessGroup, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    public string BuildSecurityFilter(UserClaimsContext userClaims)
    {
        if (userClaims.PermittedMatters.Count == 0)
            return string.Empty;

        var clauses = userClaims.PermittedMatters
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"matterId eq '{EscapeODataString(m)}'");
        return $"({string.Join(" or ", clauses)})";
    }

    public string BuildAclFilter(UserClaimsContext userClaims)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(userClaims.Email))
            clauses.Add($"allowedUsers/any(u: u eq '{EscapeODataString(userClaims.Email)}')");

        foreach (var group in userClaims.Groups)
        {
            if (!string.IsNullOrWhiteSpace(group))
                clauses.Add($"allowedGroups/any(g: g eq '{EscapeODataString(group)}')");
        }

        if (clauses.Count == 0)
            return string.Empty;

        return $"({string.Join(" or ", clauses)})";
    }

    private static IEnumerable<string> ParseMatterIds(string rawClaimValue)
    {
        if (string.IsNullOrWhiteSpace(rawClaimValue))
            yield break;

        var trimmed = rawClaimValue.Trim();

        if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            var parsedFromJson = TryParseMatterIdsFromJson(trimmed);
            if (parsedFromJson.Count > 0)
            {
                foreach (var matter in parsedFromJson)
                    yield return matter;

                yield break;
            }
        }

        foreach (var token in Regex.Split(rawClaimValue, "[,;\\s]+"))
        {
            if (!string.IsNullOrWhiteSpace(token))
                yield return token.Trim();
        }
    }

    private static List<string> TryParseMatterIdsFromJson(string json)
    {
        var parsed = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;

                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        parsed.Add(value.Trim());
                }

                return parsed;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var propName in new[] { "matters", "matterIds", "ids" })
                {
                    if (!root.TryGetProperty(propName, out var property) || property.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var item in property.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                            continue;

                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            parsed.Add(value.Trim());
                    }

                    if (parsed.Count > 0)
                        break;
                }
            }
        }
        catch
        {
        }

        return parsed;
    }

    private static string EscapeODataString(string input) => input.Replace("'", "''", StringComparison.Ordinal);
}
