using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

public sealed class RetrievalService : IRetrievalService
{
    private readonly IIndexVersionService _indexVersionService;
    private readonly OpenAI.Embeddings.EmbeddingClient _embeddingClient;
    private readonly ILogger<RetrievalService> _logger;
    private readonly int _embeddingNeighbors;
    private readonly int _topChunks;
    private readonly int _maxChunkChars;
    private readonly int? _embeddingDimensions;
    private readonly string[] _permittedMatterClaimTypes;
    private readonly List<string> _baseSelectFields = new()
    {
        "content",
        "source",
        "sourceFile",
        "page",
        "matterId",
        "practiceArea",
        "client",
        "confidentialityLevel",
        "documentVersion",
        "ingestionTimestamp",
        "checksum"
    };
    private readonly HashSet<string> _missingSelectFields = new(StringComparer.OrdinalIgnoreCase);

    public RetrievalService(
        IIndexVersionService indexVersionService,
        AzureOpenAIClient openAiClient,
        IConfiguration configuration,
        ILogger<RetrievalService> logger)
    {
        _indexVersionService = indexVersionService;
        _logger = logger;
        var embeddingDeployment = configuration["AzureOpenAI:EmbeddingDeployment"];
        if (string.IsNullOrWhiteSpace(embeddingDeployment))
            throw new InvalidOperationException("Missing configuration: AzureOpenAI:EmbeddingDeployment");

        _embeddingClient = openAiClient.GetEmbeddingClient(embeddingDeployment);
        _embeddingDimensions = configuration.GetValue<int?>("AzureOpenAI:EmbeddingDimensions");
        _embeddingNeighbors = configuration.GetValue<int?>("Rag:KNearestNeighbors") ?? 5;
        _topChunks = configuration.GetValue<int?>("Rag:TopChunks") ?? 5;
        _maxChunkChars = configuration.GetValue<int?>("Rag:MaxContextCharacters") ?? 12000;
        _permittedMatterClaimTypes = configuration.GetSection("Authorization:PermittedMattersClaimTypes").Get<string[]>()
            ?? new[] { "permittedMatters", "matter_ids", "matters", "matterId", "extension_matterIds" };
    }

    public async Task<RetrievalResult> RetrieveAsync(AskRequestDto request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userClaims = GetUserClaims(user);
        var securityFilter = BuildSecurityFilter(userClaims);
        if (string.IsNullOrWhiteSpace(securityFilter))
            throw new UnauthorizedAccessException("No permitted matters found for current user.");

        if (!userClaims.PermittedMatters.Contains(request.MatterId))
            throw new UnauthorizedAccessException($"Matter access denied for {request.MatterId}.");

        var filters = new List<string>
        {
            securityFilter,
            $"matterId eq '{EscapeODataString(request.MatterId)}'"
        };

        if (!string.IsNullOrWhiteSpace(request.PracticeArea))
            filters.Add($"practiceArea eq '{EscapeODataString(request.PracticeArea)}'");

        if (!string.IsNullOrWhiteSpace(request.Client))
            filters.Add($"client eq '{EscapeODataString(request.Client)}'");

        if (!string.IsNullOrWhiteSpace(request.ConfidentialityLevel))
            filters.Add($"confidentialityLevel eq '{EscapeODataString(request.ConfidentialityLevel)}'");

        var searchFilter = string.Join(" and ", filters);

        var embeddingOptions = new OpenAI.Embeddings.EmbeddingGenerationOptions();
        if (_embeddingDimensions.HasValue)
            embeddingOptions.Dimensions = _embeddingDimensions.Value;

        var embeddingResponse = await _embeddingClient.GenerateEmbeddingAsync(
            request.Question,
            options: embeddingOptions,
            cancellationToken: cancellationToken);

        var questionVector = embeddingResponse.Value.ToFloats().ToArray();

        var searchOptions = new SearchOptions
        {
            Size = _topChunks,
            Filter = searchFilter,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(questionVector)
                    {
                        KNearestNeighborsCount = _embeddingNeighbors,
                        Fields = { "contentVector" }
                    }
                }
            }
        };

        foreach (var field in GetSelectFields())
        {
            searchOptions.Select.Add(field);
        }
        var searchClient = _indexVersionService.CreateSearchClientForActiveIndex();
        var searchResponse = await ExecuteSearchAsync(searchClient, request.Question, searchOptions, cancellationToken);

        var chunks = new List<RetrievedChunk>();
        var runningChars = 0;
        await foreach (var result in searchResponse.Value.GetResultsAsync())
        {
            if (!result.Document.TryGetValue("content", out var contentValue))
                continue;

            var content = contentValue?.ToString();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            if (runningChars + content.Length > _maxChunkChars)
                continue;

            var sourceFile = GetPreferredSourceFile(result.Document);
            var snippet = BuildSnippet(content, 240);
            runningChars += content.Length;

            chunks.Add(new RetrievedChunk
            {
                Content = content,
                Snippet = snippet,
                SourceFile = sourceFile,
                Page = ParseInt(result.Document.TryGetValue("page", out var pageObj) ? pageObj : null),
                Score = result.Score,
                DocumentVersion = result.Document.TryGetValue("documentVersion", out var versionObj) ? versionObj?.ToString() : null,
                IngestionTimestamp = ParseDateTimeOffset(result.Document.TryGetValue("ingestionTimestamp", out var ingestObj) ? ingestObj : null),
                Checksum = result.Document.TryGetValue("checksum", out var checksumObj) ? checksumObj?.ToString() : null,
                MatterId = result.Document.TryGetValue("matterId", out var matterObj) ? matterObj?.ToString() : null,
                PracticeArea = result.Document.TryGetValue("practiceArea", out var practiceObj) ? practiceObj?.ToString() : null,
                Client = result.Document.TryGetValue("client", out var clientObj) ? clientObj?.ToString() : null,
                ConfidentialityLevel = result.Document.TryGetValue("confidentialityLevel", out var confObj) ? confObj?.ToString() : null
            });
        }

        var averageScore = chunks.Count == 0
            ? 0.0
            : chunks.Where(c => c.Score.HasValue).Select(c => c.Score!.Value).DefaultIfEmpty(0.0).Average();

        return new RetrievalResult
        {
            Chunks = chunks,
            SearchFilter = searchFilter,
            RetrievedChunkCount = chunks.Count,
            AverageScore = averageScore,
            UserClaims = userClaims
        };
    }

    public UserClaimsContext GetUserClaims(ClaimsPrincipal user)
    {
        var userId = user.FindFirst("userId")?.Value
            ?? user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst(ClaimTypes.Upn)?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "anonymous";

        var role = user.FindFirst("role")?.Value
            ?? user.FindFirst(ClaimTypes.Role)?.Value
            ?? "unknown";

        var permittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claimType in _permittedMatterClaimTypes.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            foreach (var claim in user.FindAll(claimType))
            {
                foreach (var matter in ParseMatterIds(claim.Value))
                {
                    permittedMatters.Add(matter);
                }
            }
        }

        return new UserClaimsContext
        {
            UserId = userId,
            Role = role,
            PermittedMatters = permittedMatters
        };
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

    private static string GetPreferredSourceFile(SearchDocument document)
    {
        var sourceFile = document.TryGetValue("sourceFile", out var sf) ? sf?.ToString() : null;
        if (!string.IsNullOrWhiteSpace(sourceFile))
            return sourceFile;

        var source = document.TryGetValue("source", out var s) ? s?.ToString() : null;
        if (!string.IsNullOrWhiteSpace(source))
            return source;

        return "unknown";
    }

    private static string BuildSnippet(string content, int maxChars)
    {
        if (content.Length <= maxChars)
            return content;

        return content[..maxChars].TrimEnd() + "...";
    }

    private static int? ParseInt(object? value)
    {
        if (value is null)
            return null;

        if (value is int i)
            return i;

        if (value is long l && l <= int.MaxValue && l >= int.MinValue)
            return (int)l;

        if (int.TryParse(value.ToString(), out var parsed))
            return parsed;

        return null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(object? value)
    {
        if (value is null)
            return null;

        if (value is DateTimeOffset dto)
            return dto;

        if (value is DateTime dt)
            return new DateTimeOffset(dt, TimeSpan.Zero);

        if (DateTimeOffset.TryParse(value.ToString(), out var parsed))
            return parsed;

        return null;
    }

    private static string EscapeODataString(string value) => value.Replace("'", "''");

    private static IEnumerable<string> ParseMatterIds(string rawValue)
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
                // Fall through to delimiter parsing.
            }
        }

        return trimmed.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private IReadOnlyList<string> GetSelectFields()
    {
        return _baseSelectFields.Where(field => !_missingSelectFields.Contains(field)).ToList();
    }

    private async Task<Response<SearchResults<SearchDocument>>> ExecuteSearchAsync(
        SearchClient client,
        string query,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await client.SearchAsync<SearchDocument>(query, options, cancellationToken);
            }
            catch (RequestFailedException ex) when (TryExtractMissingField(ex, out var field))
            {
                if (_missingSelectFields.Add(field))
                {
                    options.Select.Remove(field);
                    _logger.LogWarning("Search index missing field '{Field}'; removed from select list.", field);
                    continue;
                }
            }
            catch (RequestFailedException)
            {
                throw;
            }
        }
    }

    private static bool TryExtractMissingField(RequestFailedException ex, out string fieldName)
    {
        fieldName = string.Empty;
        var match = Regex.Match(ex.Message, "property '([^']+)' does not exist", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        fieldName = match.Groups[1].Value;
        return !string.IsNullOrWhiteSpace(fieldName);
    }
}
